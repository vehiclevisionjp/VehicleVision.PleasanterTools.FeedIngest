using VehicleVision.PleasanterTools.FeedIngest.Core;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
using VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;
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

    /// <summary>終了コード: 0＝全対象が成功または変更なし、1＝一部でも保留・失敗・未処理。</summary>
    public int ExitCode => IsClean ? 0 : 1;
}

/// <summary>
/// 取り込み元ごとに、取得→解析→対応づけ→外部キー照合→作成／更新を行う。
/// </summary>
/// <remarks>
/// <para>1 記事の失敗は他の記事を止めない。認証失敗・回数制限・予算切れは、共有する送り先に対する
/// 停止事由なので、残りの記事と取り込み元を処理しない（未処理として報告し、成功に数えない）。</para>
/// <para>**永続状態・実行排他は未実装。** 条件付き取得（ETag／Last-Modified）は使わず毎回取得し、
/// 同じ DB を使う複数プロセスの排他もしない（同じ送り先へ同時に実行しないこと）。</para>
/// </remarks>
public sealed class IngestRunner(
    FeedIngestConfiguration configuration,
    Func<SourceConfiguration, FeedFetcher> fetcherFactory,
    ExternalKeyUpserter? upserter,
    Func<string, string?>? environment,
    TextWriter output)
{
    /// <param name="dryRun">true なら Pleasanter へ一切接続せず、取得・解析・対応づけまで確認する。</param>
    public async Task<IngestReport> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (!dryRun && upserter is null)
        {
            throw new InvalidOperationException("dry-run 以外では送り先が必要です。");
        }

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
                stoppedBy = await ProcessAsync(source, report, dryRun, cancellationToken).ConfigureAwait(false);
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
        SourceConfiguration source, SourceReport report, bool dryRun, CancellationToken token)
    {
        FeedFetchResult fetched;
        try
        {
            using var fetcher = fetcherFactory(source);
            fetched = await fetcher
                .FetchAsync(source.Url, source.Http.Authentication, null, environment, token)
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
            // バッチを部分確定しない
            report.Error = $"parse-{ex.Kind}";
            return null;
        }

        report.Held += parsed.ItemErrors.Count + parsed.Conflicts.Count;

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

        for (var index = 0; index < parsed.Entries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var mapped = mapper.Map(parsed.Entries[index]);
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

            var result = await upserter!
                .UpsertAsync(source.Target.SiteId, mapped.Record!, source.Target.Mode, token)
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
                default:
                    // Unauthorized・RateLimited・BudgetExhausted: 実行を止め、残りは未処理
                    report.Pending += parsed.Entries.Count - index;
                    return result.Outcome.ToString();
            }
        }

        return null;
    }

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
