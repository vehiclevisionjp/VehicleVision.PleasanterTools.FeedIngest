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
    [InlineData("unknown")]
    [InlineData("check", "extra")]
    [InlineData("migrate", "extra")]
    [InlineData("unlock", "extra")]
    [InlineData("ingest")]
    [InlineData("ingest", "--config")]
    [InlineData("ingest", "--dry-run")]
    [InlineData("ingest", "--config", "a.json", "--config", "b.json")]
    [InlineData("ingest", "--config", "a.json", "--unknown")]
    public async Task 使い方の誤りは終了コード2で使い方を出す(params string[] args)
    {
        var (code, _, error) = await RunAsync(args);

        Assert.Equal(2, code);
        Assert.Contains("[ERROR]", error, StringComparison.Ordinal);
        Assert.Contains("--help", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 引数なしは短い使い方をstderrへ出して終了コード2()
    {
        var (code, output, error) = await RunAsync([]);

        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("Usage:", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--config=a.json", "--dry-run")]
    [InlineData("-config", "a.json", "-dry-run")]
    [InlineData("/config:a.json", "/DRY-RUN")]
    public async Task 引数は3つの接頭辞と区切りで同じに読む(params string[] options)
    {
        var path = WriteConfig(ValidConfig.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        try
        {
            // 設定の誤りで止まる（引数が読めていれば、その設定ファイルを開く。パスは options の a.json ではなく差し替える）
            var args = new[] { "ingest" }.Concat(options.Select(o => o.Replace("a.json", path, StringComparison.Ordinal))).ToArray();
            var (code, _, error) = await RunAsync(args);

            Assert.Equal(2, code);
            Assert.Contains("schemaVersion", error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task バージョンとヘルプは標準出力へ出して終了コード0()
    {
        var version = await RunAsync(["--version"]);
        var help = await RunAsync(["ingest", "--help"]);

        Assert.Equal(0, version.Code);
        Assert.StartsWith("feedingest ", version.Out, StringComparison.Ordinal);
        Assert.Equal(0, help.Code);
        Assert.Contains("--config", help.Out, StringComparison.Ordinal);
        Assert.Empty(help.Err);
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

            var (code, _, _) = await RunAsync(["ingest", "--config", path, "--dry-run"], token: cts.Token);

            Assert.Equal(130, code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task migrateは状態DBを作りmigrateStatusは未適用なら1で適用済みなら0()
    {
        var directory = Path.Combine(Path.GetTempPath(), "feedingest-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var env = new Dictionary<string, string>
        {
            ["FEEDINGEST_DB_PROVIDER"] = "sqlite",
            ["FEEDINGEST_DB_CONNECTIONSTRING"] = $"Data Source={Path.Combine(directory, "feedingest.db")}",
        };
        string? Environment(string name) => env.GetValueOrDefault(name);
        try
        {
            var before = await RunAsync(["migrate-status"], Environment);
            var applied = await RunAsync(["migrate"], Environment);
            var after = await RunAsync(["migrate-status"], Environment);

            Assert.Equal(1, before.Code);
            Assert.Equal(0, applied.Code);
            Assert.Equal(0, after.Code);
            Assert.True(File.Exists(Path.Combine(directory, "feedingest.db")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ingestは状態DBがマイグレーション前なら通信せず終了コード2()
    {
        var directory = Path.Combine(Path.GetTempPath(), "feedingest-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = WriteConfig(ValidConfig);
        var env = new Dictionary<string, string>
        {
            ["FEEDINGEST_TEST_API_KEY"] = "test-key",
            ["FEEDINGEST_DB_PROVIDER"] = "sqlite",
            ["FEEDINGEST_DB_CONNECTIONSTRING"] = $"Data Source={Path.Combine(directory, "feedingest.db")}",
        };
        try
        {
            var (code, output, error) = await RunAsync(["ingest", "--config", path], name => env.GetValueOrDefault(name));

            Assert.Equal(2, code);
            Assert.Contains("migrate", error, StringComparison.Ordinal);
            Assert.Empty(output);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task unlockは残った排他を解除し_排他が無ければその旨を出す()
    {
        var directory = Path.Combine(Path.GetTempPath(), "feedingest-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "feedingest.db")}";
        var env = new Dictionary<string, string>
        {
            ["FEEDINGEST_DB_PROVIDER"] = "sqlite",
            ["FEEDINGEST_DB_CONNECTIONSTRING"] = connectionString,
        };
        string? Environment(string name) => env.GetValueOrDefault(name);
        try
        {
            Assert.Equal(0, (await RunAsync(["migrate"], Environment)).Code);
            var store = new VehicleVision.PleasanterTools.FeedIngest.Data.IngestStateStore(
                new VehicleVision.PleasanterTools.Core.Data.DbConnectionFactory(
                    VehicleVision.PleasanterTools.Core.Data.DatabaseProvider.Sqlite, connectionString));
            var none = await RunAsync(["unlock"], Environment);
            await store.TryBeginRunAsync("crashed");
            var released = await RunAsync(["unlock"], Environment);

            Assert.Contains("[OK] No active run lock", none.Out, StringComparison.Ordinal);
            Assert.Contains("[OK] Released", released.Out, StringComparison.Ordinal);
            Assert.Null(await store.FindActiveRunAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task 状態DBの設定が不正なら終了コード2()
    {
        var env = new Dictionary<string, string> { ["FEEDINGEST_DB_PROVIDER"] = "no-such-db" };

        var (code, _, error) = await RunAsync(["migrate"], name => env.GetValueOrDefault(name));

        Assert.Equal(2, code);
        Assert.Contains("FEEDINGEST_DB_PROVIDER", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PLEASANTER_BASE_URL")]
    [InlineData("PLEASANTER_API_KEY")]
    [InlineData("FEEDINGEST_PLEASANTER_BASE_URL")]
    [InlineData("FEEDINGEST_PLEASANTER_API_KEY")]
    public async Task 旧名だけ設定された環境は新名が未設定として終了コード2(string legacy)
    {
        var env = new Dictionary<string, string> { [legacy] = "x" };

        var (code, output, error) = await RunAsync(["check"], name => env.GetValueOrDefault(name));

        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("VVPT_PLEASANTER_", error, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer supported", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task jsonは枠つきの1行を標準出力へ出す()
    {
        var (code, output, error) = await RunAsync(["check", "--json"], _ => null);

        Assert.Equal(2, code);
        Assert.Equal(
            "{\"command\":\"check\",\"ok\":false,\"exitCode\":2,\"errors\":[{\"code\":\"Usage\","
            + "\"message\":\"Environment variable VVPT_PLEASANTER_BASE_URL must be an HTTPS URL without credentials, query or fragment.\"}],\"warnings\":[]}",
            output.TrimEnd());
        Assert.Contains("[ERROR]", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 接続確認の環境変数が無ければ終了コード2()
    {
        var (code, _, _) = await RunAsync(["check"], _ => null);

        Assert.Equal(2, code);
    }
}
