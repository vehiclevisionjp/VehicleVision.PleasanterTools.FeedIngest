using VehicleVision.PleasanterTools.Core.Cli;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter.Tests;

public sealed class PleasanterEnvironmentTests
{
    [Fact]
    public void checkはVVPT名の環境変数から接続設定を作る()
    {
        var variables = new Dictionary<string, string?>
        {
            ["VVPT_PLEASANTER_BASE_URL"] = "https://example.invalid",
            ["VVPT_PLEASANTER_API_KEY"] = "test-key",
        };
        var options = PleasanterEnvironment.FromEnvironment(name => variables.GetValueOrDefault(name));
        Assert.Equal("test-key", options.ApiKey);
        Assert.Equal("https://example.invalid", options.BaseUrl);
        Assert.Equal(1.1m, options.ApiVersion);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Theory]
    [InlineData("PLEASANTER_BASE_URL")]
    [InlineData("PLEASANTER_API_KEY")]
    [InlineData("FEEDINGEST_PLEASANTER_BASE_URL")]
    [InlineData("FEEDINGEST_PLEASANTER_API_KEY")]
    public void 旧名は値として使わない(string legacyName)
    {
        // 旧名の検出と案内は呼び出し側（CliApp）が先に行う。ここでは、旧名が新名の代わりにならないことを確かめる
        var variables = new Dictionary<string, string?>
        {
            [legacyName] = legacyName.EndsWith("URL", StringComparison.Ordinal) ? "https://example.invalid" : "do-not-log",
        };
        Assert.Throws<UsageException>(() => PleasanterEnvironment.FromEnvironment(name => variables.GetValueOrDefault(name)));
    }

    [Fact]
    public void checkは値とFILEの競合を拒否する()
    {
        var error = Assert.Throws<UsageException>(() => PleasanterEnvironment.FromEnvironment(name => name switch
        {
            "VVPT_PLEASANTER_BASE_URL" => "https://example.invalid",
            "VVPT_PLEASANTER_API_KEY" => "do-not-log",
            "VVPT_PLEASANTER_API_KEY_FILE" => "secret-path",
            _ => null,
        }));
        Assert.DoesNotContain("do-not-log", error.ToString());
    }

    [Fact]
    public void checkはAPIキーが無ければ拒否する()
    {
        var error = Assert.Throws<UsageException>(() => PleasanterEnvironment.FromEnvironment(name =>
            name == "VVPT_PLEASANTER_BASE_URL" ? "https://example.invalid" : null));
        Assert.Contains("VVPT_PLEASANTER_API_KEY", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://example.invalid")]
    [InlineData("https://user:do-not-log@example.invalid")]
    [InlineData("https://example.invalid/?secret=do-not-log")]
    public void checkの不正な接続先は送信前に拒否する(string? url)
    {
        var error = Assert.Throws<UsageException>(() => PleasanterEnvironment.FromEnvironment(name => name switch
        {
            "VVPT_PLEASANTER_BASE_URL" => url,
            "VVPT_PLEASANTER_API_KEY" => "do-not-log",
            _ => null,
        }));
        Assert.DoesNotContain("do-not-log", error.ToString());
        Assert.All(error.Message, c => Assert.True(c < 128));
    }

    [Fact]
    public async Task 検証済みJSONから要求ごとに秘密を解決する()
    {
        var key = "first";
        var provider = new ConfiguredPleasanterOptionsProvider(
            new DestinationConfiguration("office", new Uri("https://example.invalid"), 1.1m,
                new SecretReference("KEY"), "Asia/Tokyo"), name => name == "KEY" ? key : null);
        Assert.Equal("first", (await provider.GetAsync()).ApiKey);
        key = "second";
        Assert.Equal("second", (await provider.GetAsync()).ApiKey);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync(cancelled.Token));
    }

    [Fact]
    public void checkでも秘密ファイルを利用できる()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "test-key\n");
            var options = PleasanterEnvironment.FromEnvironment(name => name switch
            {
                "VVPT_PLEASANTER_BASE_URL" => "https://example.invalid",
                "VVPT_PLEASANTER_API_KEY_FILE" => file,
                _ => null,
            });
            Assert.Equal("test-key", options.ApiKey);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
