using Microsoft.Data.Sqlite;
using VehicleVision.PleasanterTools.Core.Cli.Testing;

namespace VehicleVision.PleasanterTools.FeedIngest.Cli.Tests;

/// <summary>
/// CLI の共通仕様の契約テスト（Core.Cli.Testing）。出力が ASCII・結果は stdout で診断は stderr・接頭辞・終了コード・
/// <c>--json</c> の枠・旧名の検出・秘密を出さない、を確かめる。ネットワークへは出ない（取り込みは、遮断される宛先で試す）。
/// </summary>
public sealed class CliContractTest : CliContractTests, IDisposable
{
    private const string BadSchemaConfig = """
        {
          "schemaVersion": 2,
          "pleasanter": {"id": "office", "baseUrl": "https://pleasanter.example.test", "apiVersion": 1.1,
            "apiKeyEnv": "FEEDINGEST_TEST_API_KEY", "apiKeyUserTimeZoneId": "Asia/Tokyo"},
          "sources": []
        }
        """;

    // 取得先がループバックなので、通信せずに取得の失敗（終了コード 1）になる
    private const string LoopbackSourceConfig = """
        {
          "schemaVersion": 1,
          "pleasanter": {"id": "office", "baseUrl": "https://pleasanter.example.test", "apiVersion": 1.1,
            "apiKeyEnv": "FEEDINGEST_TEST_API_KEY", "apiKeyUserTimeZoneId": "Asia/Tokyo"},
          "sources": [{
            "id": "news", "url": "https://127.0.0.1/feed", "format": "auto",
            "http": {"authentication": {"type": "none"}},
            "target": {"siteId": 1, "externalKeyColumn": "ClassZ", "mode": "upsert"},
            "mapping": [{"target": "Title", "source": "title"}]
          }]
        }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "feedingest-contract-" + Guid.NewGuid().ToString("N"));

    public CliContractTest()
    {
        Directory.CreateDirectory(_directory);
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
            // 後始末の失敗は、試験の結果にしない
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Dictionary<string, string> DatabaseEnvironment(string name) => new()
    {
        ["FEEDINGEST_DB_PROVIDER"] = "sqlite",
        ["FEEDINGEST_DB_CONNECTIONSTRING"] = $"Data Source={Path.Combine(_directory, name)}",
    };

    private async Task MigrateAsync(string name)
    {
        var env = DatabaseEnvironment(name);
        var code = await CliApp.RunAsync(["migrate"], TextWriter.Null, TextWriter.Null, key => env.GetValueOrDefault(key), CancellationToken.None);
        Assert.Equal(0, code);
    }

    protected override CliContract CreateContract()
    {
        var badSchema = WriteFile("bad-schema.json", BadSchemaConfig);
        var loopback = WriteFile("loopback.json", LoopbackSourceConfig);
        var secretEnvironment = new Dictionary<string, string>
        {
            ["VVPT_PLEASANTER_BASE_URL"] = "http://not-https.example.test/?token=secret-value",
            ["VVPT_PLEASANTER_API_KEY"] = "secret-value",
        };
        var loopbackEnvironment = new Dictionary<string, string> { ["FEEDINGEST_TEST_API_KEY"] = "secret-value" };

        return new CliContract
        {
            Name = "FeedIngest",
            Run = CliContract.RunnerFor(CliApp.Tool, CliApp.Handlers),
            Tool = CliApp.Tool,
            Scenarios =
            [
                new CliScenario { Name = "check without environment", Args = ["check"], Outcome = CliOutcome.UsageError },
                new CliScenario
                {
                    Name = "check without environment json", Args = ["check", "--json"], Outcome = CliOutcome.UsageError,
                    Json = CliJsonKind.Result, Command = "check",
                },
                new CliScenario
                {
                    Name = "check with a bad base url keeps secrets", Args = ["check"], Outcome = CliOutcome.UsageError,
                    Environment = secretEnvironment, Secrets = ["secret-value"],
                },
                new CliScenario { Name = "ingest without config", Args = ["ingest"], Outcome = CliOutcome.UsageError },
                new CliScenario
                {
                    Name = "ingest with a bad configuration", Args = ["ingest", "--config", badSchema, "--dry-run"],
                    Outcome = CliOutcome.UsageError,
                },
                new CliScenario
                {
                    Name = "ingest with a bad configuration json", Args = ["ingest", "--config", badSchema, "--dry-run", "--json"],
                    Outcome = CliOutcome.UsageError, Json = CliJsonKind.Result, Command = "ingest",
                },
                new CliScenario
                {
                    Name = "ingest dry run with a blocked source", Args = ["ingest", "--config", loopback, "--dry-run"],
                    Outcome = CliOutcome.Failed, Environment = loopbackEnvironment, Secrets = ["secret-value"],
                },
                new CliScenario
                {
                    Name = "ingest dry run with a blocked source json", Args = ["ingest", "--config", loopback, "--dry-run", "--json"],
                    Outcome = CliOutcome.Failed, Environment = loopbackEnvironment, Json = CliJsonKind.Result, Command = "ingest",
                },
                new CliScenario
                {
                    Name = "ingest interrupted", Args = ["ingest", "--config", loopback, "--dry-run"],
                    Outcome = CliOutcome.Interrupted, Environment = loopbackEnvironment,
                },
                new CliScenario
                {
                    Name = "migrate", Args = ["migrate"], Outcome = CliOutcome.Success,
                    Environment = DatabaseEnvironment("migrate.db"), RequiresOkLine = true,
                },
                new CliScenario
                {
                    Name = "migrate with a bad provider", Args = ["migrate"], Outcome = CliOutcome.UsageError,
                    Environment = new Dictionary<string, string> { ["FEEDINGEST_DB_PROVIDER"] = "no-such-db" },
                },
                new CliScenario
                {
                    Name = "migrate status pending", Args = ["migrate-status"], Outcome = CliOutcome.CheckFailed,
                    Environment = DatabaseEnvironment("pending.db"),
                },
                new CliScenario
                {
                    Name = "migrate status pending json", Args = ["migrate-status", "--json"], Outcome = CliOutcome.CheckFailed,
                    Environment = DatabaseEnvironment("pending-json.db"), Json = CliJsonKind.Result, Command = "migrate-status",
                },
                new CliScenario
                {
                    Name = "migrate status applied", Args = ["migrate-status"], Outcome = CliOutcome.Success,
                    Environment = DatabaseEnvironment("applied.db"), RequiresOkLine = true,
                    Arrange = () => MigrateAsync("applied.db"),
                },
                new CliScenario
                {
                    Name = "migrate status applied json", Args = ["migrate-status", "--json"], Outcome = CliOutcome.Success,
                    Environment = DatabaseEnvironment("applied-json.db"), Json = CliJsonKind.Result, Command = "migrate-status",
                    Arrange = () => MigrateAsync("applied-json.db"),
                },
                new CliScenario
                {
                    Name = "unlock", Args = ["unlock"], Outcome = CliOutcome.Success,
                    Environment = DatabaseEnvironment("unlock.db"), RequiresOkLine = true,
                    Arrange = () => MigrateAsync("unlock.db"),
                },
                new CliScenario
                {
                    Name = "unlock before migrate", Args = ["unlock"], Outcome = CliOutcome.UsageError,
                    Environment = DatabaseEnvironment("unlock-pending.db"),
                },
            ],
            ArgumentForms =
            [
                new CliArgumentForms
                {
                    Name = "ingest options",
                    Forms =
                    [
                        ["ingest", "--config", badSchema, "--dry-run"],
                        ["ingest", "--config=" + badSchema, "--dry-run"],
                        ["ingest", "-config", badSchema, "-dry-run"],
                        ["ingest", "/config:" + badSchema, "/dry-run"],
                        ["INGEST", "--CONFIG=" + badSchema, "--Dry-Run"],
                    ],
                },
            ],
            LegacyEnvironment = new CliLegacyEnvironmentProbe { Args = ["check"] },
        };
    }
}
