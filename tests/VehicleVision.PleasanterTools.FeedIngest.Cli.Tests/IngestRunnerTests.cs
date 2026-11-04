using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.FeedIngest.Cli.Ingest;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
using VehicleVision.PleasanterTools.FeedIngest.Feeds;
using VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

namespace VehicleVision.PleasanterTools.FeedIngest.Cli.Tests;

public sealed class IngestRunnerTests
{
    private const string ApiKey = "secret-api-key";

    internal static string Rss(params string[] items)
        => $"""<?xml version="1.0" encoding="UTF-8"?><rss version="2.0"><channel><title>t</title>{string.Concat(items)}</channel></rss>""";

    internal static string Item(string guid, string title = "題名")
        => $"<item><guid>{guid}</guid><title>{title}</title><link>https://news.example.test/{guid}</link></item>";

    internal static FeedIngestConfiguration Config(string mappingExtra = "", bool secondSource = false, string secondAuth = """{"type": "none"}""")
    {
        var second = secondSource
            ? $$$"""
              ,{
                "id": "second", "url": "https://second.example.test/feed", "format": "auto",
                "http": {"authentication": {{{secondAuth}}}},
                "target": {"siteId": 222, "externalKeyColumn": "ClassZ", "mode": "upsert"},
                "mapping": [{"target": "Title", "source": "title"}]
              }
              """
            : string.Empty;
        return ConfigurationReader.Parse($$$"""
            {
              "schemaVersion": 1,
              "pleasanter": {"id": "office", "baseUrl": "https://pleasanter.example.test", "apiVersion": 1.1,
                "apiKeyEnv": "TEST_API_KEY", "apiKeyUserTimeZoneId": "Asia/Tokyo"},
              "sources": [{
                "id": "first", "url": "https://first.example.test/feed", "format": "auto",
                "http": {"authentication": {"type": "none"}},
                "target": {"siteId": 111, "externalKeyColumn": "ClassZ", "mode": "upsert"},
                "mapping": [{"target": "Title", "source": "title"}, {"target": "ClassA", "source": "link"}{{{mappingExtra}}}]
              }{{{second}}}]
            }
            """);
    }

    internal sealed class FakeFeeds(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    internal static HttpResponseMessage Xml(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/rss+xml") };

    internal sealed class FakePleasanter : HttpMessageHandler
    {
        public List<JsonObject> Rows { get; } = [];

        public List<string> Paths { get; } = [];

        public Func<string, HttpResponseMessage?>? Hook { get; set; }

        private long _next = 100;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            if (Hook?.Invoke(path) is { } injected)
            {
                return injected;
            }

            if (path.EndsWith("/Get", StringComparison.Ordinal))
            {
                var filter = body["View"]!["ColumnFilterHash"]!.AsObject().Single();
                var matches = Rows.Where(r => (string?)r["ClassHash"]?[filter.Key] == (string?)filter.Value).ToList();
                return Json(new JsonObject
                {
                    ["StatusCode"] = 200,
                    ["Response"] = new JsonObject
                    {
                        ["Offset"] = 0, ["PageSize"] = 200, ["TotalCount"] = matches.Count,
                        ["Data"] = new JsonArray(matches.Select(r => (JsonNode)r.DeepClone()).ToArray()),
                    },
                });
            }

            if (path.EndsWith("/Create", StringComparison.Ordinal))
            {
                var row = new JsonObject { ["ResultId"] = _next++ };
                Merge(row, body);
                Rows.Add(row);
                return Json(new JsonObject { ["Id"] = (long)row["ResultId"]!, ["StatusCode"] = 200 });
            }

            var id = long.Parse(path.Split('/')[^2]);
            Merge(Rows.Single(r => (long)r["ResultId"]! == id), body);
            return Json(new JsonObject { ["Id"] = id, ["StatusCode"] = 200 });
        }

        private static void Merge(JsonObject row, JsonObject body)
        {
            foreach (var (name, value) in body)
            {
                if (name is "ApiKey" or "ApiVersion" || value is null)
                {
                    continue;
                }

                if (value is JsonObject hash)
                {
                    var target = row[name] as JsonObject ?? new JsonObject();
                    foreach (var (column, columnValue) in hash)
                    {
                        target[column] = columnValue?.DeepClone();
                    }

                    row[name] = target;
                }
                else
                {
                    row[name] = value.DeepClone();
                }
            }
        }
    }

    internal static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static (IngestRunner Runner, StringWriter Output) Create(
        FeedIngestConfiguration config, FakeFeeds feeds, FakePleasanter? pleasanter,
        Func<string, string?>? environment = null)
    {
        var output = new StringWriter();
        ExternalKeyUpserter? upserter = pleasanter is null
            ? null
            : new ExternalKeyUpserter(
                new PleasanterGateway(
                pleasanter,
                new PleasanterOptions { BaseUrl = "https://pleasanter.example.test", ApiKey = ApiKey }),
                new UpsertOptions { MinInterval = TimeSpan.Zero },
                (_, _) => Task.CompletedTask);
        var runner = new IngestRunner(
            config,
            source => new FeedFetcher(
                feeds,
                new FeedFetchOptions { MaxBodyBytes = source.Http.MaxBodyBytes },
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }),
                (_, _) => Task.CompletedTask),
            upserter,
            environment,
            output);
        return (runner, output);
    }

    [Fact]
    public async Task 初回は作成し2回目は何も作らず終了コードは0()
    {
        var feeds = new FakeFeeds(_ => Xml(Rss(Item("a"), Item("b"))));
        var pleasanter = new FakePleasanter();
        var (runner, output) = Create(Config(), feeds, pleasanter);

        var first = await runner.RunAsync(false, CancellationToken.None);
        var second = await runner.RunAsync(false, CancellationToken.None);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(2, first.Sources[0].Created);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(0, second.Sources[0].Created);
        Assert.Equal(2, second.Sources[0].Unchanged);
        Assert.Equal(2, pleasanter.Rows.Count);
        Assert.Contains("source=first created=2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task フィードの内容が変われば更新し_所有列だけを送る()
    {
        var title = "旧題名";
        var feeds = new FakeFeeds(_ => Xml(Rss(Item("a", title))));
        var pleasanter = new FakePleasanter();
        var (runner, _) = Create(Config(), feeds, pleasanter);
        await runner.RunAsync(false, CancellationToken.None);
        pleasanter.Rows[0]["Body"] = "利用者の本文";
        title = "新題名";

        var report = await runner.RunAsync(false, CancellationToken.None);

        Assert.Equal(1, report.Sources[0].Updated);
        Assert.Equal("新題名", (string?)pleasanter.Rows[0]["Title"]);
        Assert.Equal("利用者の本文", (string?)pleasanter.Rows[0]["Body"]);
    }

    [Fact]
    public async Task 項目エラーと競合と対応づけエラーは保留にして他の記事は続け終了コードは1()
    {
        var items = Rss(
            Item("ok"),
            "<item><title>ID無し</title></item>",
            Item("dup", "1"),
            Item("dup", "2"),
            "<item><guid>no-title</guid></item>");
        var config = Config(", {\"target\": \"Body\", \"source\": \"contentText\", \"onMissing\": \"error\"}");
        var feeds = new FakeFeeds(_ => Xml(items));
        var pleasanter = new FakePleasanter();
        var (runner, _) = Create(config, feeds, pleasanter);

        var report = await runner.RunAsync(false, CancellationToken.None);

        // Body が無い記事は対応づけエラー（ok・no-title の 2 件）、ID 無しは項目エラー、dup は競合
        Assert.Equal(4, report.Sources[0].Held);
        Assert.Equal(0, report.Sources[0].Created);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public async Task 一部の記事が保留でも正常な記事は作成する()
    {
        var feeds = new FakeFeeds(_ => Xml(Rss(Item("ok"), "<item><title>ID無し</title></item>")));
        var pleasanter = new FakePleasanter();
        var (runner, _) = Create(Config(), feeds, pleasanter);

        var report = await runner.RunAsync(false, CancellationToken.None);

        Assert.Equal(1, report.Sources[0].Created);
        Assert.Equal(1, report.Sources[0].Held);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public async Task 取得に失敗した取り込み元があっても次の取り込み元は処理する()
    {
        var feeds = new FakeFeeds(request => request.RequestUri!.Host == "first.example.test"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Xml(Rss(Item("a"))));
        var pleasanter = new FakePleasanter();
        var (runner, output) = Create(Config(secondSource: true), feeds, pleasanter);

        var report = await runner.RunAsync(false, CancellationToken.None);

        Assert.Equal("fetch-HttpStatus", report.Sources[0].Error);
        Assert.Equal(1, report.Sources[1].Created);
        Assert.Equal(1, report.ExitCode);
        Assert.Contains("error=fetch-HttpStatus", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解析できないフィードはバッチを部分確定せずPleasanterへ送らない()
    {
        var feeds = new FakeFeeds(_ => Xml("""{"json": "feed"}"""));
        var pleasanter = new FakePleasanter();
        var (runner, _) = Create(Config(), feeds, pleasanter);

        var report = await runner.RunAsync(false, CancellationToken.None);

        Assert.StartsWith("parse-", report.Sources[0].Error, StringComparison.Ordinal);
        Assert.Empty(pleasanter.Paths);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public async Task Pleasanterの認証失敗は残りを処理せず未処理として止める()
    {
        var feeds = new FakeFeeds(_ => Xml(Rss(Item("a"), Item("b"))));
        var pleasanter = new FakePleasanter { Hook = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var (runner, output) = Create(Config(secondSource: true), feeds, pleasanter);

        var report = await runner.RunAsync(false, CancellationToken.None);

        Assert.Equal("Unauthorized", report.StoppedBy);
        Assert.Equal(2, report.Sources[0].Pending);
        Assert.True(report.Sources[1].NotProcessed);
        Assert.Single(pleasanter.Paths);
        Assert.Single(feeds.Requests);
        Assert.Equal(1, report.ExitCode);
        Assert.Contains("stopped reason=Unauthorized", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task dryRunはPleasanterへ接続せず対応づけまで確認する()
    {
        var feeds = new FakeFeeds(_ => Xml(Rss(Item("a"), Item("b"))));
        var (runner, _) = Create(Config(), feeds, pleasanter: null);

        var report = await runner.RunAsync(true, CancellationToken.None);

        Assert.Equal(2, report.Sources[0].Mapped);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task 送り先が無いのにdryRunでなければ設定エラー()
    {
        var (runner, _) = Create(Config(), new FakeFeeds(_ => Xml(Rss())), pleasanter: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task 取り込み元ごとの資格情報は別の取り込み元へ送らない()
    {
        var env = new Dictionary<string, string> { ["U"] = "user", ["P"] = "pass" };
        var config = Config(
            secondSource: true,
            secondAuth: """{"type": "basic", "usernameEnv": "U", "passwordEnv": "P"}""");
        var feeds = new FakeFeeds(_ => Xml(Rss(Item("a"))));
        var (runner, _) = Create(config, feeds, new FakePleasanter(), name => env.GetValueOrDefault(name));

        await runner.RunAsync(false, CancellationToken.None);

        Assert.False(feeds.Requests.Single(r => r.RequestUri!.Host == "first.example.test").Headers.Contains("Authorization"));
        Assert.True(feeds.Requests.Single(r => r.RequestUri!.Host == "second.example.test").Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task 秘密を解決できない取り込み元は秘密名以外を出さず失敗にする()
    {
        var config = Config(
            secondSource: true,
            secondAuth: """{"type": "basic", "usernameEnv": "U", "passwordEnv": "P"}""");
        var feeds = new FakeFeeds(_ => Xml(Rss(Item("a"))));
        var (runner, _) = Create(config, feeds, new FakePleasanter(), _ => null);

        var report = await runner.RunAsync(false, CancellationToken.None);

        Assert.Equal("secret", report.Sources[1].Error);
        Assert.Equal(1, report.Sources[0].Created);
    }

    [Fact]
    public async Task 出力にURLと記事の内容とAPIキーを含めない()
    {
        var feeds = new FakeFeeds(_ => Xml(Rss(Item("secret-guid", "秘密の題名"))));
        var pleasanter = new FakePleasanter();
        var (runner, output) = Create(Config(), feeds, pleasanter);

        await runner.RunAsync(false, CancellationToken.None);

        var text = output.ToString();
        Assert.DoesNotContain("example.test", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-guid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("秘密の題名", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 中断はキャンセルとして伝える()
    {
        using var cts = new CancellationTokenSource();
        var feeds = new FakeFeeds(_ =>
        {
            cts.Cancel();
            return Xml(Rss(Item("a")));
        });
        var (runner, _) = Create(Config(), feeds, new FakePleasanter());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(false, cts.Token));
    }
}
