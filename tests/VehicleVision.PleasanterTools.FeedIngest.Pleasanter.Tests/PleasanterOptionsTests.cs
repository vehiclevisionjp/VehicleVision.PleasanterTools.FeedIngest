using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter.Tests;

public sealed class PleasanterOptionsTests
{
    [Theory]
    [InlineData("PLEASANTER_")]
    [InlineData("FEEDINGEST_PLEASANTER_")]
    public void 旧checkと新しい名前空間を単独で利用できる(string prefix)
    {
        var variables = new Dictionary<string, string?>
        {
            [prefix + "BASE_URL"] = "https://example.invalid",
            [prefix + "API_KEY"] = "test-key",
        };
        var options = PleasanterOptions.FromEnvironment(name => variables.GetValueOrDefault(name));
        Assert.Equal("test-key", options.ApiKey);
        Assert.Equal("https://example.invalid", options.BaseUrl);
        Assert.Equal(1.1m, options.ApiVersion);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Fact]
    public void 名前空間の混在と秘密の競合を拒否する()
    {
        var variables = new Dictionary<string, string?>
        {
            ["PLEASANTER_BASE_URL"] = "https://example.invalid",
            ["FEEDINGEST_PLEASANTER_API_KEY"] = "do-not-log",
        };
        var error = Assert.Throws<InvalidOperationException>(() =>
            PleasanterOptions.FromEnvironment(name => variables.GetValueOrDefault(name)));
        Assert.DoesNotContain("do-not-log", error.ToString());
    }

    [Fact]
    public void checkは値とFILEの競合を拒否する()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PleasanterOptions.FromEnvironment(name => name switch
        {
            "PLEASANTER_BASE_URL" => "https://example.invalid",
            "PLEASANTER_API_KEY" => "do-not-log",
            "PLEASANTER_API_KEY_FILE" => "secret-path",
            _ => null,
        }));
        Assert.DoesNotContain("do-not-log", error.ToString());
        Assert.DoesNotContain("secret-path", error.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://example.invalid")]
    [InlineData("https://user:do-not-log@example.invalid")]
    [InlineData("https://example.invalid/?secret=do-not-log")]
    public void checkの不正な接続先は送信前に拒否する(string? url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PleasanterOptions.FromEnvironment(name => name switch
        {
            "PLEASANTER_BASE_URL" => url,
            "PLEASANTER_API_KEY" => "do-not-log",
            _ => null,
        }));
        Assert.DoesNotContain("do-not-log", error.ToString());
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
            var options = PleasanterOptions.FromEnvironment(name => name switch
            {
                "PLEASANTER_BASE_URL" => "https://example.invalid",
                "PLEASANTER_API_KEY_FILE" => file,
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
