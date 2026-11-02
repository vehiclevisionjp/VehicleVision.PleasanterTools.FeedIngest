using VehicleVision.PleasanterTools.FeedIngest.Core;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
using VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;
using VehicleVision.PleasanterTools.FeedIngest.Core.State;
using VehicleVision.PleasanterTools.FeedIngest.Feeds;
using VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

namespace VehicleVision.PleasanterTools.FeedIngest.Cli.Ingest;

/// <summary>取り込み元 1 つの結果。記事の本文・元 ID・URL を含めない。</summary>
public sealed class SourceReport(string sourceId)
{
    public string SourceId { get; } = sourceId;

    public int Created { get; set; }

    public int Updated { get; set; }

    /// <summary>変更なし、または createOnly で既存に触れなかった記事。</summary>
    public int Unchanged { get; set; }

    /// <summary>保留（項目の不正・対応づけの誤り・同一 ID の競合・複数一致・作成結果の不明）。</summary>
    public int Held { get; set; }

    public int Failed { get; set; }

    /// <summary>未処理（実行を止めたため送っていない記事）。</summary>
    public int Pending { get; set; }

    /// <summary>dry-run で対応づけまで成功した記事。</summary>
    public int Mapped { get; set; }

    /// <summary>取り込み元全体の失敗の分類（取得・解析・設定）。無ければ <c>null</c>。</summary>
    public string? Error { get; set; }

    /// <summary>実行が止まったため、この取り込み元は処理しなかった。</summary>
    public bool NotProcessed { get; set; }

    public bool IsClean => Error is null && !NotProcessed && Held == 0 && Failed == 0 && Pending == 0;
}

/// <summary>実行全体の結果。</summary>
public sealed record IngestReport(IReadOnlyList<SourceReport> Sources, string? StoppedBy)
{
    public bool IsClean => StoppedBy is null && Sources.All(source => source.IsClean);

    /// <summary>終了コード: 0＝全対象が成功または変更なし、1＝一部でも保留・失敗・未処理・排他を取れなかった。</summary>
    public int ExitCode => IsClean ? 0 : 1;
}

/// <summary>
/// 取り込み元ごとに、取得→解析→対応づけ→外部キー照合→作成／更新を行う。
/// </summary>
/// <remarks>
/// <para>1 記事の失敗は他の記事を止めない。認証失敗・回数制限・予算切れは、共有する送り先に対する
/// 停止事由なので、残りの記事と取り込み元を処理しない（未処理として報告し、成功に数えない）。</para>
/// <para><b>状態（<paramref name="state"/>）がある場合</b>（dry-run 以外の通常実行）:
/// 開始時に実行の排他を取り（取れなければ何もせず非ゼロ）、前回送信中のまま残った配送を Unknown に戻す。
/// 取得は保存済みの検証子で条件付きにし、<b>解析・保存に成功してから</b>検証子を進める（200 を受けただけでは進めない）。
/// 配送は、保存済みの記事のスナップショットから、取得とは独立に処理する（304 でも、取得に失敗しても、未送信は再開する）。
/// 前回送った値ハッシュと対応づけの版が同じなら API を呼ばない（利用者が所有列を編集していても、毎回は戻さない。U5）。</para>
/// <para>状態が無い場合は、毎回取得し、既存レコードの所有列が取り込み値と違えば更新する。</para>
/// </remarks>
public sealed class IngestRunner(
    FeedIngestConfiguration configuration,
    Func<SourceConfiguration, FeedFetcher> fetcherFactory,
    ExternalKeyUpserter? upserter,
    Func<string, string?>? environment,
    TextWriter output,
    IIngestStateStore? state = null,
    string? ownerId = null,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <param name="dryRun">true なら Pleasanter へも状態 DB へも接続せず、取得・解析・対応づけまで確認する。</param>
    public async Task<IngestReport> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (!dryRun && upserter is null)
        {
            throw new InvalidOperationException("dry-run 以外では送り先が必要です。");
        }

        var store = dryRun ? null : state;
        RunLease? lease = null;
        if (store is not null)
        {
            lease = await store.TryBeginRunAsync(ownerId ?? "ingest", cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                // 他の実行が終わっていない。何も取得・送信しない
                output.WriteLine("stopped reason=Locked");
                return new IngestReport(
                    configuration.Sources.Select(source => new SourceReport(source.Id) { NotProcessed = true }).ToList(),
                    "Locked");
            }
        }

        var status = "Failed";
        try
        {
            if (store is not null)
            {
                await store.MarkInterruptedAsUnknownAsync(configuration.Pleasanter.Id, cancellationToken).ConfigureAwait(false);
            }

            var report = await RunSourcesAsync(dryRun, store, cancellationToken).ConfigureAwait(false);
            status = report.IsClean ? "Succeeded" : "Incomplete";
            return report;
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled";
            throw;
        }
        finally
        {
            if (store is not null && lease is not null)
            {
                await store.EndRunAsync(lease.Id, status, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<IngestReport> RunSourcesAsync(bool dryRun, IIngestStateStore? store, CancellationToken cancellationToken)
    {
        var reports = new List<SourceReport>();
        string? stoppedBy = null;
        foreach (var source in configuration.Sources)
        {
            var report = new SourceReport(source.Id);
            reports.Add(report);
            if (stoppedBy is not null)
            {
                report.NotProcessed = true;
            }
            else
            {
                stoppedBy = await ProcessAsync(source, report, dryRun, store, cancellationToken).ConfigureAwait(false);
            }

            Write(report);
        }

        if (stoppedBy is not null)
        {
            output.WriteLine($"stopped reason={stoppedBy}");
        }

        return new IngestReport(reports, stoppedBy);
    }

    /// <summary>1 つの取り込み元を処理する。実行全体を止める事由があれば、その分類を返す。</summary>
    private async Task<string?> ProcessAsync(
        SourceConfiguration source, SourceReport report, bool dryRun, IIngestStateStore? store, CancellationToken token)
    {
        var fresh = await FetchAsync(source, report, store, token).ConfigureAwait(false);

        // 状態があるときは、取得の成否にかかわらず、保存済みの記事（未送信の再開を含む）を処理する
        IReadOnlyList<NormalizedEntry> entries;
        if (store is not null)
        {
            entries = (await store.ListEntriesAsync(source.Id, token).ConfigureAwait(false)).Select(stored => stored.Entry).ToList();
        }
        else
        {
            entries = fresh ?? [];
        }

        if (entries.Count == 0)
        {
            return null;
        }

        EntryMapper mapper;
        try
        {
            mapper = new EntryMapper(configuration.Pleasanter, source);
        }
        catch (TimeZoneNotFoundException)
        {
            report.Error = "timezone";
            return null;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var mapped = mapper.Map(entries[index]);
            if (!mapped.IsSuccess)
            {
                report.Held++;
                continue;
            }

            if (dryRun)
            {
                report.Mapped++;
                continue;
            }

            var stop = await DeliverAsync(source, report, mapper, mapped.Record!, store, token).ConfigureAwait(false);
            if (stop is not null)
            {
                report.Pending += entries.Count - index;
                return stop;
            }
        }

        return null;
    }

    /// <summary>
    /// フィードを取得・解析する。状態があれば、成功した結果だけを（検証子とともに）同じトランザクションで確定する。
    /// 304・取得の失敗・解析の失敗では <c>null</c>（状態があるときは保存済みの記事を使う）。
    /// </summary>
    private async Task<IReadOnlyList<NormalizedEntry>?> FetchAsync(
        SourceConfiguration source, SourceReport report, IIngestStateStore? store, CancellationToken token)
    {
        var version = SourceConfigVersion.Of(source);
        FeedValidators? validators = null;
        var hadEntries = false;
        if (store is not null)
        {
            var saved = await store.GetSourceAsync(source.Id, token).ConfigureAwait(false);
            hadEntries = (await store.ListEntriesAsync(source.Id, token).ConfigureAwait(false)).Count > 0;
            if (saved is not null && !string.Equals(saved.ConfigVersion, version, StringComparison.Ordinal))
            {
                // URL・認証が変わった。古い検証子を別の取得先へ送らない
                await store.ClearValidatorsAsync(source.Id, token).ConfigureAwait(false);
            }
            else if (saved is not null && hadEntries && (saved.ETag is not null || saved.LastModified is not null))
            {
                // 既存バッチ（保存済みの記事）が無い 304 は成功にできないので、その場合は条件付きにしない
                validators = new FeedValidators(saved.ETag, saved.LastModified);
            }
        }

        FeedFetchResult fetched;
        try
        {
            using var fetcher = fetcherFactory(source);
            fetched = await fetcher
                .FetchAsync(source.Url, source.Http.Authentication, validators, environment, token)
                .ConfigureAwait(false);
        }
        catch (FeedFetchException ex)
        {
            report.Error = $"fetch-{ex.Kind}";
            return null;
        }
        catch (InvalidOperationException)
        {
            // 秘密参照を解決できない（値・パスは出さない）
            report.Error = "secret";
            return null;
        }

        if (fetched.NotModified)
        {
            return null;
        }

        FeedParseResult parsed;
        try
        {
            parsed = FeedParser.Parse(
                fetched.Body!,
                source.Id,
                fetched.FinalUrl,
                source.Format,
                fetched.Charset,
                new FeedParserLimits(MaxBytes: source.Http.MaxBodyBytes));
        }
        catch (FeedParseException ex)
        {
            // バッチを部分確定しない（検証子も進めない）
            report.Error = $"parse-{ex.Kind}";
            return null;
        }

        report.Held += parsed.ItemErrors.Count + parsed.Conflicts.Count;

        if (store is not null)
        {
            await store.CommitFetchAsync(
                new FetchCommit(
                    source.Id,
                    version,
                    fetched.Validators.ETag,
                    fetched.Validators.LastModified,
                    _time.GetUtcNow(),
                    parsed.Entries),
                token).ConfigureAwait(false);
        }

        return parsed.Entries;
    }

    /// <summary>1 記事を送り先へ届ける。実行全体を止める事由があれば、その分類を返す。</summary>
    private async Task<string?> DeliverAsync(
        SourceConfiguration source, SourceReport report, EntryMapper mapper, MappedRecord record,
        IIngestStateStore? store, CancellationToken token)
    {
        Delivery? previous = null;
        if (store is not null)
        {
            previous = await store.GetDeliveryAsync(
                configuration.Pleasanter.Id, source.Target.SiteId, record.ExternalKey, token).ConfigureAwait(false);
            if (previous is { State: DeliveryState.Succeeded }
                && (source.Target.Mode == TargetMode.CreateOnly
                    || (string.Equals(previous.ValueHash, record.ValueHash, StringComparison.Ordinal)
                        && string.Equals(previous.MappingVersion, mapper.Version, StringComparison.Ordinal))))
            {
                // 送った値も対応づけも変わっていない。API を呼ばない（利用者の編集も戻さない）
                report.Unchanged++;
                return null;
            }

            await SaveAsync(store, source, record, previous, DeliveryState.Sending, previous?.RecordId, previous?.LastOutcome, countAttempt: true, token)
                .ConfigureAwait(false);
        }

        var result = await upserter!
            .UpsertAsync(source.Target.SiteId, record, source.Target.Mode, token)
            .ConfigureAwait(false);
        switch (result.Outcome)
        {
            case UpsertOutcome.Created:
                report.Created++;
                break;
            case UpsertOutcome.Updated:
                report.Updated++;
                break;
            case UpsertOutcome.Unchanged:
            case UpsertOutcome.SkippedExisting:
                report.Unchanged++;
                break;
            case UpsertOutcome.Held:
                report.Held++;
                break;
            case UpsertOutcome.Failed:
                report.Failed++;
                break;
        }

        if (store is not null)
        {
            await RecordAsync(store, source, mapper, record, previous, result, token).ConfigureAwait(false);
        }

        return result.Outcome is UpsertOutcome.Unauthorized or UpsertOutcome.RateLimited or UpsertOutcome.BudgetExhausted
            ? result.Outcome.ToString()
            : null;
    }

    private async Task RecordAsync(
        IIngestStateStore store, SourceConfiguration source, EntryMapper mapper, MappedRecord record, Delivery? previous,
        UpsertResult result, CancellationToken token)
    {
        switch (result.Outcome)
        {
            case UpsertOutcome.Created:
            case UpsertOutcome.Updated:
            case UpsertOutcome.Unchanged:
            case UpsertOutcome.SkippedExisting:
                // 送った（または既存と一致を確認した）値を記録する
                await store.SaveDeliveryAsync(
                    new DeliverySave(
                        configuration.Pleasanter.Id, source.Target.SiteId, source.Id, record.ExternalKey,
                        DeliveryState.Succeeded, result.RecordId, mapper.Version, record.ValueHash,
                        result.Outcome.ToString(), CountAttempt: false),
                    token).ConfigureAwait(false);
                break;
            case UpsertOutcome.Held:
            case UpsertOutcome.Failed:
                await SaveAsync(store, source, record, previous, DeliveryState.Held, result.RecordId, result.Outcome.ToString(), countAttempt: false, token)
                    .ConfigureAwait(false);
                break;
            default:
                // 認証失敗・回数制限・予算切れ: 書き込みが届いたか分からないので Unknown（次回、外部キーで照合する）
                await SaveAsync(store, source, record, previous, DeliveryState.Unknown, result.RecordId, result.Outcome.ToString(), countAttempt: false, token)
                    .ConfigureAwait(false);
                break;
        }
    }

    /// <summary>前回送った値・版は保ったまま、状態だけを更新する（送れていない値を「送った」と記録しない）。</summary>
    private async Task SaveAsync(
        IIngestStateStore store, SourceConfiguration source, MappedRecord record, Delivery? previous, DeliveryState stateValue,
        long? recordId, string? outcome, bool countAttempt, CancellationToken token)
        => await store.SaveDeliveryAsync(
            new DeliverySave(
                configuration.Pleasanter.Id, source.Target.SiteId, source.Id, record.ExternalKey, stateValue,
                recordId, previous?.MappingVersion, previous?.ValueHash, outcome, countAttempt),
            token).ConfigureAwait(false);

    private void Write(SourceReport report)
    {
        if (report.NotProcessed)
        {
            output.WriteLine($"source={report.SourceId} not-processed");
            return;
        }

        output.WriteLine(
            $"source={report.SourceId} created={report.Created} updated={report.Updated} unchanged={report.Unchanged}"
            + $" held={report.Held} failed={report.Failed} pending={report.Pending} mapped={report.Mapped}"
            + (report.Error is null ? string.Empty : $" error={report.Error}"));
    }
}
