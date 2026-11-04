using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.FeedIngest.Cli.Ingest;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
using VehicleVision.PleasanterTools.FeedIngest.Core.State;
using VehicleVision.PleasanterTools.FeedIngest.Data;
using VehicleVision.PleasanterTools.FeedIngest.Feeds;
using VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

namespace VehicleVision.PleasanterTools.FeedIngest.Cli.Tests;

/// <summary>状態 DB（一時 SQLite の実 DB）を使う取り込みの試験。</summary>
public sealed class IngestRunnerStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "feedingest-state-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;
    private readonly IngestStateStore _store;

    public IngestRunnerStateTests()
    {
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "feedingest.db")}";
        FeedIngestDatabase.Instance.Migrator.MigrateUp(DatabaseProvider.Sqlite, _connectionString);
        _store = new IngestStateStore(new DbConnectionFactory(DatabaseProvider.Sqlite, _connectionString));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 一時ファイル
        }
    }

    private sealed class Run
    {
        public required IngestRunner Runner { get; init; }

        public required StringWriter Output { get; init; }
    }

    private Run Create(FeedIngestConfiguration config, IngestRunnerTests.FakeFeeds feeds, IngestRunnerTests.FakePleasanter pleasanter,
        IIngestStateStore? state = null)
    {
        var output = new StringWriter();
        var upserter = new ExternalKeyUpserter(
            new PleasanterGateway(
                pleasanter,
                new PleasanterOptions { BaseUrl = "https://pleasanter.example.test", ApiKey = "secret-api-key" }),
            new UpsertOptions { MinInterval = TimeSpan.Zero },
            (_, _) => Task.CompletedTask);
        return new Run
        {
            Output = output,
            Runner = new IngestRunner(
                config,
                source => new FeedFetcher(
                    feeds,
                    new FeedFetchOptions { MaxBodyBytes = source.Http.MaxBodyBytes },
                    (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }),
                    (_, _) => Task.CompletedTask),
                upserter,
                null,
                output,
                state ?? _store,
                "test-owner"),
        };
    }

    private static HttpResponseMessage WithEtag(string body, string etag)
    {
        var response = IngestRunnerTests.Xml(body);
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    /// <summary>If-None-Match が ETag と一致すれば 304、そうでなければ本文を返すフィード。</summary>
    private static IngestRunnerTests.FakeFeeds Conditional(Func<string> body, string etag = "\"v1\"")
        => new(request => request.Headers.TryGetValues("If-None-Match", out var values) && values.Contains(etag)
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : WithEtag(body(), etag));

    [Fact]
    public async Task 二回目は条件付きで取得し_何も送らず_APIを1回も呼ばない()
    {
        var feeds = Conditional(() => IngestRunnerTests.Rss(IngestRunnerTests.Item("a"), IngestRunnerTests.Item("b")));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        var run = Create(IngestRunnerTests.Config(), feeds, pleasanter);

        var first = await run.Runner.RunAsync(false, CancellationToken.None);
        var callsAfterFirst = pleasanter.Paths.Count;
        var second = await run.Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal(2, first.Sources[0].Created);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(2, second.Sources[0].Unchanged);
        Assert.Equal(callsAfterFirst, pleasanter.Paths.Count);
        Assert.Equal("\"v1\"", feeds.Requests[1].Headers.GetValues("If-None-Match").Single());
        Assert.Equal("\"v1\"", (await _store.GetSourceAsync("first"))!.ETag);
    }

    [Fact]
    public async Task 利用者が編集した所有列は_送る値が変わらない限り戻さない()
    {
        var feeds = Conditional(() => IngestRunnerTests.Rss(IngestRunnerTests.Item("a", "元の題名")));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        var run = Create(IngestRunnerTests.Config(), feeds, pleasanter);
        await run.Runner.RunAsync(false, CancellationToken.None);
        pleasanter.Rows[0]["Title"] = "利用者が直した題名";

        await run.Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal("利用者が直した題名", (string?)pleasanter.Rows[0]["Title"]);
    }

    [Fact]
    public async Task 送る値が変われば更新して_その値を記録する()
    {
        var title = "旧題名";
        var feeds = new IngestRunnerTests.FakeFeeds(_ => IngestRunnerTests.Xml(IngestRunnerTests.Rss(IngestRunnerTests.Item("a", title))));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        var run = Create(IngestRunnerTests.Config(), feeds, pleasanter);
        await run.Runner.RunAsync(false, CancellationToken.None);
        title = "新題名";

        var report = await run.Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal(1, report.Sources[0].Updated);
        Assert.Equal("新題名", (string?)pleasanter.Rows[0]["Title"]);
        var third = await run.Runner.RunAsync(false, CancellationToken.None);
        Assert.Equal(1, third.Sources[0].Unchanged);
    }

    [Fact]
    public async Task 送り先が止まっても取得は確定し_304でも保存済みの記事から未送信を再開する()
    {
        var feeds = Conditional(() => IngestRunnerTests.Rss(IngestRunnerTests.Item("a"), IngestRunnerTests.Item("b")));
        var pleasanter = new IngestRunnerTests.FakePleasanter { Hook = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var run = Create(IngestRunnerTests.Config(), feeds, pleasanter);

        var first = await run.Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal("Unauthorized", first.StoppedBy);
        Assert.Equal(2, first.Sources[0].Pending);
        Assert.Equal(2, (await _store.ListEntriesAsync("first")).Count);
        Assert.Equal("\"v1\"", (await _store.GetSourceAsync("first"))!.ETag);

        pleasanter.Hook = null;
        var second = await run.Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(2, second.Sources[0].Created);
        Assert.Equal(2, pleasanter.Rows.Count);
        Assert.Equal("\"v1\"", feeds.Requests[1].Headers.GetValues("If-None-Match").Single());
    }

    [Fact]
    public async Task 対応づけを変えると304でも保存済みの記事を新しい設定で再評価する()
    {
        var feeds = Conditional(() => IngestRunnerTests.Rss(IngestRunnerTests.Item("a")));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        await Create(IngestRunnerTests.Config(), feeds, pleasanter).Runner.RunAsync(false, CancellationToken.None);

        var changed = IngestRunnerTests.Config(", {\"target\": \"ClassC\", \"constant\": \"added\"}");
        var report = await Create(changed, feeds, pleasanter).Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal(1, report.Sources[0].Updated);
        Assert.Equal("added", (string?)pleasanter.Rows[0]["ClassHash"]!["ClassC"]);
        Assert.Equal("\"v1\"", feeds.Requests[1].Headers.GetValues("If-None-Match").Single());
    }

    [Fact]
    public async Task 解析に失敗した200は検証子も記事も進めず_保存済みの記事は処理する()
    {
        var broken = false;
        var feeds = Conditional(() => broken ? """{"not": "a feed"}""" : IngestRunnerTests.Rss(IngestRunnerTests.Item("a")), etag: "\"v1\"");
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        var run = Create(IngestRunnerTests.Config(), feeds, pleasanter);
        await run.Runner.RunAsync(false, CancellationToken.None);
        await _store.ClearValidatorsAsync("first");
        broken = true;

        var report = await run.Runner.RunAsync(false, CancellationToken.None);

        Assert.StartsWith("parse-", report.Sources[0].Error, StringComparison.Ordinal);
        Assert.Null((await _store.GetSourceAsync("first"))!.ETag);
        Assert.Single(await _store.ListEntriesAsync("first"));
        Assert.Equal(1, report.Sources[0].Unchanged);
    }

    [Fact]
    public async Task URLや認証が変わったら古い検証子を送らない()
    {
        var feeds = Conditional(() => IngestRunnerTests.Rss(IngestRunnerTests.Item("a")));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        await Create(IngestRunnerTests.Config(), feeds, pleasanter).Runner.RunAsync(false, CancellationToken.None);
        var moved = ConfigurationReader.Parse(MovedConfigJson());

        await Create(moved, feeds, pleasanter).Runner.RunAsync(false, CancellationToken.None);

        Assert.False(feeds.Requests[1].Headers.Contains("If-None-Match"));
        Assert.Equal(SourceConfigVersion.Of(moved.Sources[0]), (await _store.GetSourceAsync("first"))!.ConfigVersion);
    }

    private static string MovedConfigJson() => """
        {
          "schemaVersion": 1,
          "pleasanter": {"id": "office", "baseUrl": "https://pleasanter.example.test", "apiVersion": 1.1,
            "apiKeyEnv": "TEST_API_KEY", "apiKeyUserTimeZoneId": "Asia/Tokyo"},
          "sources": [{
            "id": "first", "url": "https://moved.example.test/feed", "format": "auto",
            "http": {"authentication": {"type": "none"}},
            "target": {"siteId": 111, "externalKeyColumn": "ClassZ", "mode": "upsert"},
            "mapping": [{"target": "Title", "source": "title"}, {"target": "ClassA", "source": "link"}]
          }]
        }
        """;

    [Fact]
    public async Task 取得バージョンはURLと認証名で変わり_秘密の値を含まない()
    {
        var a = IngestRunnerTests.Config().Sources[0];
        var b = IngestRunnerTests.Config(secondSource: true, secondAuth: """{"type": "basic", "usernameEnv": "U", "passwordEnv": "P"}""").Sources[1];
        var c = IngestRunnerTests.Config(secondSource: true, secondAuth: """{"type": "basic", "usernameEnv": "U2", "passwordEnv": "P"}""").Sources[1];

        Assert.Equal(SourceConfigVersion.Of(a), SourceConfigVersion.Of(IngestRunnerTests.Config().Sources[0]));
        Assert.NotEqual(SourceConfigVersion.Of(b), SourceConfigVersion.Of(c));
        Assert.NotEqual(SourceConfigVersion.Of(a), SourceConfigVersion.Of(b));
        Assert.Equal(64, SourceConfigVersion.Of(a).Length);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 排他を取れなければ取得も送信もせず_非ゼロで終了する()
    {
        var feeds = Conditional(() => IngestRunnerTests.Rss(IngestRunnerTests.Item("a")));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        await _store.TryBeginRunAsync("other-process");
        var run = Create(IngestRunnerTests.Config(), feeds, pleasanter);

        var report = await run.Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal("Locked", report.StoppedBy);
        Assert.Equal(1, report.ExitCode);
        Assert.Empty(feeds.Requests);
        Assert.Empty(pleasanter.Paths);
        Assert.Contains("stopped reason=Locked", run.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal("other-process", (await _store.FindActiveRunAsync())!.OwnerId);
    }

    [Fact]
    public async Task 終了後も中断でも排他を解放する()
    {
        var feeds = Conditional(() => IngestRunnerTests.Rss(IngestRunnerTests.Item("a")));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        await Create(IngestRunnerTests.Config(), feeds, pleasanter).Runner.RunAsync(false, CancellationToken.None);
        Assert.Null(await _store.FindActiveRunAsync());

        using var cts = new CancellationTokenSource();
        var cancelling = new IngestRunnerTests.FakeFeeds(_ =>
        {
            cts.Cancel();
            return IngestRunnerTests.Xml(IngestRunnerTests.Rss(IngestRunnerTests.Item("z")));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(IngestRunnerTests.Config(), cancelling, pleasanter).Runner.RunAsync(false, cts.Token));

        Assert.Null(await _store.FindActiveRunAsync());
    }

    [Fact]
    public async Task 前回送信中のまま止まった配送は照合で解決し_二重に作らない()
    {
        var feeds = new IngestRunnerTests.FakeFeeds(_ => IngestRunnerTests.Xml(IngestRunnerTests.Rss(IngestRunnerTests.Item("a"))));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        // 1 回目: 作成は届いたが、結果を記録する前に落ちた状態を作る
        // （DotNetClient は取り消し以外の例外を Unknown にするため、取り消しで中断を模す）
        using var interrupt = new CancellationTokenSource();
        pleasanter.Hook = path =>
        {
            if (!path.EndsWith("/Create", StringComparison.Ordinal))
            {
                return null;
            }

            interrupt.Cancel();
            throw new OperationCanceledException(interrupt.Token);
        };
        var first = Create(IngestRunnerTests.Config(), feeds, pleasanter);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Runner.RunAsync(false, interrupt.Token));
        pleasanter.Hook = null;
        var entry = (await _store.ListEntriesAsync("first")).Single();
        var key = entry.ExternalKey;
        // 実際には作成が適用されていた
        var applied = new JsonObject
        {
            ["SiteId"] = 111, ["ResultId"] = 500, ["Title"] = "題名",
            ["ClassHash"] = new JsonObject { ["ClassZ"] = key, ["ClassA"] = "https://news.example.test/a" },
        };
        pleasanter.Rows.Add(applied);
        Assert.Equal(DeliveryState.Sending, (await _store.GetDeliveryAsync("office", 111, key))!.State);

        var report = await Create(IngestRunnerTests.Config(), feeds, pleasanter).Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Single(pleasanter.Rows);
        var delivery = (await _store.GetDeliveryAsync("office", 111, key))!;
        Assert.Equal(DeliveryState.Succeeded, delivery.State);
        Assert.Equal(500, delivery.RecordId);
        Assert.Equal(2, delivery.AttemptCount);
    }

    [Fact]
    public async Task 保留した記事は保留として残し_次回に再び照合する()
    {
        var feeds = new IngestRunnerTests.FakeFeeds(_ => IngestRunnerTests.Xml(IngestRunnerTests.Rss(IngestRunnerTests.Item("a"))));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        var run = Create(IngestRunnerTests.Config(), feeds, pleasanter);
        await run.Runner.RunAsync(false, CancellationToken.None);
        var key = (await _store.ListEntriesAsync("first")).Single().ExternalKey;
        // 同じ外部キーが 2 件になった（人が解決するまで保留）
        pleasanter.Rows.Add((JsonObject)pleasanter.Rows[0].DeepClone());
        pleasanter.Rows[1]["ResultId"] = 999;
        pleasanter.Rows[0]["Title"] = "変えた";
        await _store.SaveDeliveryAsync(new DeliverySave("office", 111, "first", key, DeliveryState.Succeeded, 100, "old-mapping", "old-hash", "Created", false));

        var report = await run.Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal(1, report.Sources[0].Held);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(DeliveryState.Held, (await _store.GetDeliveryAsync("office", 111, key))!.State);
    }

    [Fact]
    public async Task dryRunは状態DBを使わず排他も取らない()
    {
        var feeds = new IngestRunnerTests.FakeFeeds(_ => IngestRunnerTests.Xml(IngestRunnerTests.Rss(IngestRunnerTests.Item("a"))));
        await _store.TryBeginRunAsync("other-process");
        var run = Create(IngestRunnerTests.Config(), feeds, new IngestRunnerTests.FakePleasanter());

        var report = await run.Runner.RunAsync(true, CancellationToken.None);

        Assert.Equal(1, report.Sources[0].Mapped);
        Assert.Equal(0, report.ExitCode);
        Assert.Null(await _store.GetSourceAsync("first"));
        Assert.Empty(await _store.ListEntriesAsync("first"));
    }

    [Fact]
    public async Task 状態の中に秘密や本文のログを出さない()
    {
        var feeds = new IngestRunnerTests.FakeFeeds(_ => IngestRunnerTests.Xml(IngestRunnerTests.Rss(IngestRunnerTests.Item("secret-guid", "秘密の題名"))));
        var pleasanter = new IngestRunnerTests.FakePleasanter();
        var run = Create(IngestRunnerTests.Config(), feeds, pleasanter);

        await run.Runner.RunAsync(false, CancellationToken.None);

        var text = run.Output.ToString();
        Assert.DoesNotContain("secret-guid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("秘密の題名", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-api-key", text, StringComparison.Ordinal);
    }
}
