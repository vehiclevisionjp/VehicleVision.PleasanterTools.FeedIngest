using System.Globalization;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
using VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

/// <summary>1 記事の取り込みの結果。</summary>
public enum UpsertOutcome
{
    /// <summary>新規に作成した（応答不明からの照合で確認した場合を含む）。</summary>
    Created,

    /// <summary>既存を更新した。</summary>
    Updated,

    /// <summary>既存の所有列が対応づけ後の値と同じなので、何も送らなかった。</summary>
    Unchanged,

    /// <summary>createOnly で既存があったので触らなかった。</summary>
    SkippedExisting,

    /// <summary>人の判断まで保留（外部キーが複数件・作成結果を確認できない・更新を確認できない）。</summary>
    Held,

    /// <summary>設定・権限・検証の誤りで、再送しても通らない。検索を完走できなかった場合も含む。</summary>
    Failed,

    /// <summary>認証に失敗した。この実行を止める。</summary>
    Unauthorized,

    /// <summary>待機指定が長い、または回数制限。この実行を止め、次回へ回す。</summary>
    RateLimited,

    /// <summary>API 呼び出しの予算を使い切った。未処理は次回へ回す。成功に数えない。</summary>
    BudgetExhausted,
}

/// <summary>取り込み結果。理由に本文・API キー・Pleasanter のメッセージを含めない。</summary>
/// <param name="Outcome">結果。</param>
/// <param name="RecordId">対象のレコード ID（分かれば）。</param>
/// <param name="Reason">理由（保留・失敗のとき）。</param>
/// <param name="StatusCode">直近の HTTP ステータス（あれば）。</param>
public sealed record UpsertResult(UpsertOutcome Outcome, long? RecordId = null, string? Reason = null, int? StatusCode = null);

/// <summary>API 呼び出しの制限と再試行の設定。既定値はアーキテクチャ方針 7 章。</summary>
public sealed record UpsertOptions
{
    /// <summary>1 回の実行で送る API 要求の上限（検索・照合を含む）。</summary>
    public int MaxRequests { get; init; } = 1000;

    /// <summary>要求と要求の最低間隔。同時要求は 1。</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>読み取り・照合の再試行の最大回数（初回に加える）。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Retry-After がこれより長ければ再試行せず次回へ回す。</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>外部キー検索の 1 回の走査で読む最大ページ数。</summary>
    public int MaxPages { get; init; } = 100;
}

/// <summary>
/// 外部キー列で既存レコードを探し、作成・更新する。
/// </summary>
/// <remarks>
/// <para>**0 件を「未作成」と解釈するのは、検索が完走し、返却値を序数比較で再検査した結果だけ。**
/// 権限不足・不正 JSON・ページ欠落・応答形の不一致は 0 件にしない（重複作成を防ぐ）。</para>
/// <para>書き込みの 5xx・408・429・通信断・Id の無い成功応答は、適用済みかもしれないので Unknown として、
/// 外部キーで照合し直す（初回＋最大 3 回、1/2/4 秒）。0 件のままなら保留にし、Create を自動で再送しない。</para>
/// <para>永続状態を持たないため「前回送った値」を知らない。既存の所有列が対応づけ後の値と違えば更新する
/// （利用者が所有列を編集していれば、実行のたびに取り込み値へ戻る。設計 U5 の
/// 「送信対象が変わったときだけ上書き」には、状態を保存する Data の実装が必要）。</para>
/// <para>確認した Pleasanter ソースは 1.5.9.1（664c833b144d9da9b6bb58e60f7e48eb01141e58）。実機では未確認。</para>
/// </remarks>
public sealed class ExternalKeyUpserter
{
    /// <summary><c>Column.SearchTypes.ExactMatch</c> の値（確認ソースでは 2）。</summary>
    private const int ExactMatch = 2;

    private static readonly int[] BackoffSeconds = [1, 2, 4];

    private readonly PleasanterGateway _client;
    private readonly UpsertOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requests;
    private long? _lastRequestAt;

    public ExternalKeyUpserter(
        PleasanterGateway client,
        UpsertOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options ?? new UpsertOptions();
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>これまでに送った API 要求の数。</summary>
    public int RequestCount => _requests;

    public async Task<UpsertResult> UpsertAsync(
        long siteId, MappedRecord record, TargetMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            return await UpsertCoreAsync(siteId, record, mode, cancellationToken).ConfigureAwait(false);
        }
        catch (StopException stop)
        {
            return stop.Result;
        }
        catch (LookupFailedException failed)
        {
            return failed.Result;
        }
    }

    private async Task<UpsertResult> UpsertCoreAsync(
        long siteId, MappedRecord record, TargetMode mode, CancellationToken token)
    {
        var lookup = await LookupAsync(siteId, record, token).ConfigureAwait(false);
        switch (lookup.Matches.Count)
        {
            case > 1:
                return new UpsertResult(UpsertOutcome.Held, null, "同じ外部キーのレコードが複数あります。人が解決するまで保留します。");
            case 1:
                return await ExistingAsync(siteId, record, mode, lookup.Matches[0], token).ConfigureAwait(false);
        }

        var create = await SendAsync(
            () => _client.CreateAsync(siteId, record.ToCreatePayload(), token), token).ConfigureAwait(false);
        if (create.IsSuccess && create.Id is > 0)
        {
            return new UpsertResult(UpsertOutcome.Created, create.Id, null, create.StatusCode);
        }

        StopIfFatal(create);
        if (create.ErrorKind is PleasanterErrorKind.Permanent)
        {
            return new UpsertResult(
                UpsertOutcome.Failed, null, "作成が拒否されました（設定・権限・値の検証を確認してください）。", create.StatusCode);
        }

        // Transient・Unknown・Id の無い成功応答: 適用済みかもしれない。外部キーで照合し直す
        return await ReconcileCreateAsync(siteId, record, mode, token).ConfigureAwait(false);
    }

    private async Task<UpsertResult> ReconcileCreateAsync(
        long siteId, MappedRecord record, TargetMode mode, CancellationToken token)
    {
        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            await _delay(Backoff(attempt), token).ConfigureAwait(false);
            Lookup lookup;
            try
            {
                lookup = await LookupAsync(siteId, record, token).ConfigureAwait(false);
            }
            catch (StopException stop) when (stop.Result.Outcome is UpsertOutcome.BudgetExhausted)
            {
                return Pending("作成結果を確認できないまま、API の予算を使い切りました。");
            }
            catch (LookupFailedException)
            {
                // 読み直せなかった。0 件とは解釈せず、次の照合へ進む
                continue;
            }

            if (lookup.Matches.Count > 1)
            {
                return new UpsertResult(UpsertOutcome.Held, null, "作成の照合で同じ外部キーのレコードが複数見つかりました。");
            }

            if (lookup.Matches.Count == 1)
            {
                var result = await ExistingAsync(siteId, record, mode, lookup.Matches[0], token).ConfigureAwait(false);
                return result.Outcome is UpsertOutcome.Unchanged or UpsertOutcome.Updated or UpsertOutcome.SkippedExisting
                    ? result with { Outcome = UpsertOutcome.Created, Reason = "応答が不明だったが、照合で作成済みを確認した。" }
                    : result;
            }
        }

        return Pending(
            "作成の結果を確認できません。元の要求が処理中の可能性があるため、Create を再送せず保留します。"
            + "運用者が未適用を確認してから明示的に再実行してください。");
    }

    private static TimeSpan Backoff(int attempt)
        => TimeSpan.FromSeconds(BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)]);

    private static UpsertResult Pending(string reason) => new(UpsertOutcome.Held, null, reason);

    private async Task<UpsertResult> ExistingAsync(
        long siteId, MappedRecord record, TargetMode mode, Row existing, CancellationToken token)
    {
        if (mode == TargetMode.CreateOnly)
        {
            return new UpsertResult(UpsertOutcome.SkippedExisting, existing.Id);
        }

        if (existing.OwnedColumnsEqual(record))
        {
            return new UpsertResult(UpsertOutcome.Unchanged, existing.Id);
        }

        for (var attempt = 0; ; attempt++)
        {
            var update = await SendAsync(
                () => _client.UpdateAsync(existing.Id, record.ToUpdatePayload(), token), token).ConfigureAwait(false);
            if (update.IsSuccess)
            {
                return new UpsertResult(UpsertOutcome.Updated, existing.Id, null, update.StatusCode);
            }

            StopIfFatal(update);
            if (update.ErrorKind is PleasanterErrorKind.Permanent)
            {
                return new UpsertResult(
                    UpsertOutcome.Failed, existing.Id, "更新が拒否されました（設定・権限・値の検証を確認してください）。", update.StatusCode);
            }

            // 適用済みかもしれない。読み直して同じなら完了、違えば同じ所有列値だけを再送する
            if (attempt >= _options.MaxRetries)
            {
                return Pending("更新の結果を確認できません。");
            }

            await _delay(Backoff(attempt), token).ConfigureAwait(false);
            try
            {
                var lookup = await LookupAsync(siteId, record, token).ConfigureAwait(false);
                if (lookup.Matches.Count > 1)
                {
                    return new UpsertResult(UpsertOutcome.Held, null, "同じ外部キーのレコードが複数あります。");
                }

                if (lookup.Matches.Count == 1 && lookup.Matches[0].OwnedColumnsEqual(record))
                {
                    return new UpsertResult(UpsertOutcome.Updated, existing.Id, "応答が不明だったが、照合で更新済みを確認した。");
                }
            }
            catch (LookupFailedException)
            {
                // 読み直せなければ、同じ値の再送で続ける
            }
        }
    }

    // 検索

    private sealed record Lookup(IReadOnlyList<Row> Matches);

    /// <summary>検索を完走できなかった。「0 件」とは解釈しない。</summary>
    private sealed class LookupFailedException(UpsertResult result) : Exception(result.Reason)
    {
        public UpsertResult Result { get; } = result;
    }

    /// <summary>この実行を止める事情（認証失敗・回数制限・予算切れ）。</summary>
    private sealed class StopException(UpsertResult result) : Exception(result.Reason)
    {
        public UpsertResult Result { get; } = result;
    }

    private static LookupFailedException LookupFailure(string reason, int? status = null)
        => new(new UpsertResult(UpsertOutcome.Failed, null, reason + "検索の失敗を 0 件とはみなしません。", status));

    /// <summary>外部キー列で全ページを走査する。完走できなければ <see cref="LookupFailedException"/>。</summary>
    private async Task<Lookup> LookupAsync(long siteId, MappedRecord record, CancellationToken token)
    {
        var column = record.ExternalKeyColumn;
        var view = new Dictionary<string, object?>
        {
            ["ColumnFilterHash"] = new Dictionary<string, object?> { [column] = record.ExternalKey },
            ["ColumnFilterSearchTypes"] = new Dictionary<string, object?> { [column] = ExactMatch },
        };

        var matches = new List<Row>();
        var offset = 0;
        for (var page = 0; ; page++)
        {
            if (page >= _options.MaxPages)
            {
                throw LookupFailure("外部キーの検索が上限のページ数を超えました。");
            }

            var offsetNow = offset;
            var response = await ReadWithRetryAsync(
                () => _client.GetPageAsync(siteId, view, offsetNow, token), token).ConfigureAwait(false);
            var data = response.Body?["Response"]?["Data"] as JsonArray;
            var total = response.Body?["Response"]?["TotalCount"] is JsonValue totalValue
                && totalValue.TryGetValue<long>(out var totalCount)
                    ? totalCount
                    : (long?)null;
            if (!response.IsSuccess || data is null || total is null)
            {
                throw LookupFailure("外部キーの検索結果を読めません（権限・応答形を確認してください）。", response.StatusCode);
            }

            foreach (var item in data)
            {
                var row = Row.TryRead(item, siteId, column, record.ExternalKey);
                if (row is not null)
                {
                    matches.Add(row);
                }
            }

            offset += data.Count;
            if (offset >= total)
            {
                return new Lookup(matches);
            }

            if (data.Count == 0)
            {
                throw LookupFailure("外部キーの検索のページが欠落しています。");
            }
        }
    }

    // 送信（同時 1・最低間隔・予算・再試行）

    /// <summary>読み取りを、408・429・5xx・通信断に限って再試行する。</summary>
    private async Task<PleasanterResponse> ReadWithRetryAsync(
        Func<Task<PleasanterResponse>> call, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await SendAsync(call, token).ConfigureAwait(false);
            if (response.IsSuccess)
            {
                return response;
            }

            StopIfFatal(response);
            if (response.ErrorKind is PleasanterErrorKind.Permanent)
            {
                return response;
            }

            if (response.RetryAfter is { } requested && requested > _options.MaxRetryAfter)
            {
                throw new StopException(new UpsertResult(
                    UpsertOutcome.RateLimited, null, "待機指定が長いため、次回へ回します。", response.StatusCode));
            }

            if (attempt >= _options.MaxRetries)
            {
                if (response.StatusCode == 429)
                {
                    throw new StopException(new UpsertResult(
                        UpsertOutcome.RateLimited, null, "回数制限が続いたため、次回へ回します。", 429));
                }

                return response;
            }

            var wait = Backoff(attempt);
            if (response.RetryAfter is { } retryAfter && retryAfter > wait)
            {
                wait = retryAfter;
            }

            await _delay(wait, token).ConfigureAwait(false);
        }
    }

    private async Task<PleasanterResponse> SendAsync(Func<Task<PleasanterResponse>> call, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_requests >= _options.MaxRequests)
            {
                throw new StopException(new UpsertResult(
                    UpsertOutcome.BudgetExhausted, null, "API 呼び出しの予算を使い切りました。未処理は次回へ回します。"));
            }

            if (_lastRequestAt is { } last)
            {
                var elapsed = _time.GetElapsedTime(last);
                if (elapsed < _options.MinInterval)
                {
                    await _delay(_options.MinInterval - elapsed, token).ConfigureAwait(false);
                }
            }

            _requests++;
            try
            {
                return await call().ConfigureAwait(false);
            }
            finally
            {
                _lastRequestAt = _time.GetTimestamp();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void StopIfFatal(PleasanterResponse response)
    {
        if (response.ErrorKind is PleasanterErrorKind.Unauthorized)
        {
            throw new StopException(new UpsertResult(
                UpsertOutcome.Unauthorized, null, "認証に失敗しました（API キー・権限を確認してください）。", response.StatusCode));
        }
    }

    // 応答の行

    /// <summary>検索結果の 1 行。外部キーが一致した行だけを表す。</summary>
    private sealed class Row
    {
        private readonly JsonObject _data;

        private Row(long id, JsonObject data)
        {
            Id = id;
            _data = data;
        }

        public long Id { get; }

        /// <summary>
        /// 外部キーが序数で一致する行だけ返す（部分一致の混入を除く）。
        /// 一致しているのにレコード ID を読めない行は、0 件と誤解しないよう検索失敗にする。
        /// </summary>
        public static Row? TryRead(JsonNode? item, long siteId, string column, string key)
        {
            if (item is not JsonObject row)
            {
                return null;
            }

            var actual = row["ClassHash"] is JsonObject classes && classes[column] is JsonValue value
                && value.TryGetValue<string>(out var text)
                    ? text
                    : null;
            if (!string.Equals(actual, key, StringComparison.Ordinal))
            {
                return null;
            }

            if (ReadLong(row["SiteId"]) is { } rowSite && rowSite != siteId)
            {
                return null;
            }

            // Results は ResultId、Issues は IssueId。違いはここに閉じ込める
            var id = ReadLong(row["ResultId"]) ?? ReadLong(row["IssueId"]);
            return id is > 0
                ? new Row(id.Value, row)
                : throw LookupFailure("外部キーが一致した行のレコード ID を読めません。");
        }

        /// <summary>対応づけ後の所有列がすべて同じか。型が読めなければ「違う」とみなす（更新は冪等）。</summary>
        public bool OwnedColumnsEqual(MappedRecord record)
        {
            foreach (var (column, expected) in record.Columns)
            {
                var actual = column is "Title" or "Body"
                    ? _data[column]
                    : (_data[MappedRecord.HashName(column)] as JsonObject)?[column];
                if (!Same(column, expected, actual))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Same(string column, object expected, JsonNode? actual)
        {
            if (actual is not JsonValue value)
            {
                return false;
            }

            switch (expected)
            {
                case string text when column.StartsWith("Date", StringComparison.Ordinal):
                    return value.TryGetValue<string>(out var dateText)
                        && DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
                        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b)
                        && a == b;
                case string text:
                    return value.TryGetValue<string>(out var actualText)
                        && string.Equals(actualText, text, StringComparison.Ordinal);
                case decimal number:
                    return ReadDecimal(value) is { } d && d == number;
                case bool flag:
                    return value.TryGetValue<bool>(out var f) && f == flag;
                default:
                    return false;
            }
        }

        private static decimal? ReadDecimal(JsonValue value)
        {
            if (value.TryGetValue<decimal>(out var number))
            {
                return number;
            }

            return value.TryGetValue<string>(out var text)
                && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
        }

        private static long? ReadLong(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue<long>(out var number))
            {
                return number;
            }

            return value.TryGetValue<string>(out var text)
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
        }
    }
}
