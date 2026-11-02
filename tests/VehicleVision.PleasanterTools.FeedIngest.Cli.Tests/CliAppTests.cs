namespace VehicleVision.PleasanterTools.FeedIngest.Cli.Tests;

public sealed class CliAppTests
{
    private const string ValidConfig = """
        {
          "schemaVersion": 1,
          "pleasanter": {"id": "office", "baseUrl": "https://pleasanter.example.test", "apiVersion": 1.1,
            "apiKeyEnv": "FEEDINGEST_TEST_API_KEY", "apiKeyUserTimeZoneId": "Asia/Tokyo"},
          "sources": [{
            "id": "news", "url": "https://feeds.example.test/feed", "format": "auto",
            "http": {"authentication": {"type": "none"}},
            "target": {"siteId": 1, "externalKeyColumn": "ClassZ", "mode": "upsert"},
            "mapping": [{"target": "Title", "source": "title"}]
          }]
        }
        """;

    private static async Task<(int Code, string Out, string Err)> RunAsync(
        string[] args, Func<string, string?>? environment = null, CancellationToken token = default)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApp.RunAsync(args, output, error, environment ?? (_ => null), token);
        return (code, output.ToString(), error.ToString());
    }

    private static string WriteConfig(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "feedingest-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData]
    [InlineData("unknown")]
    [InlineData("check", "extra")]
    [InlineData("ingest")]
    [InlineData("ingest", "--config")]
    [InlineData("ingest", "--dry-run")]
    [InlineData("ingest", "--config", "a.json", "--config", "b.json")]
    [InlineData("ingest", "--config", "a.json", "--unknown")]
    public async Task 使い方の誤りは終了コード2で使い方を出す(params string[] args)
    {
        var (code, _, error) = await RunAsync(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 設定ファイルが読めなければパスを出さず終了コード2()
    {
        var path = Path.Combine(Path.GetTempPath(), "no-such-dir-feedingest", "secret-name.json");

        var (code, _, error) = await RunAsync(["ingest", "--config", path]);

        Assert.Equal(2, code);
        Assert.DoesNotContain("secret-name", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 設定の誤りは行とキーを示して終了コード2で通信しない()
    {
        var path = WriteConfig(ValidConfig.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        try
        {
            var (code, output, error) = await RunAsync(["ingest", "--config", path, "--dry-run"]);

            Assert.Equal(2, code);
            Assert.Contains("schemaVersion", error, StringComparison.Ordinal);
            Assert.Empty(output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task APIキーを解決できなければ終了コード2で通信しない()
    {
        var path = WriteConfig(ValidConfig);
        try
        {
            var (code, output, error) = await RunAsync(["ingest", "--config", path], _ => null);

            Assert.Equal(2, code);
            Assert.Contains("FEEDINGEST_TEST_API_KEY", error, StringComparison.Ordinal);
            Assert.Empty(output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 中断は終了コード130()
    {
        var path = WriteConfig(ValidConfig);
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var (code, _, error) = await RunAsync(["ingest", "--config", path, "--dry-run"], token: cts.Token);

            Assert.Equal(130, code);
            Assert.Contains("Cancelled", error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 接続確認の環境変数が無ければ終了コード2()
    {
        var (code, _, _) = await RunAsync(["check"], _ => null);

        Assert.Equal(2, code);
    }
}
