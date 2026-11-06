using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Tests;

/// <summary>内部ホストの明示許可（U11）の、設定の形式検査と常に拒否する範囲。</summary>
public sealed class DestinationRulesTests
{
    private const string Valid = """
        {
          "schemaVersion": 1,
          "pleasanter": {
            "id": "office", "baseUrl": "https://example.invalid", "apiVersion": 1.1,
            "apiKeyEnv": "TEST_API_KEY", "apiKeyUserTimeZoneId": "Asia/Tokyo"
          },
          "sources": [{
            "id": "news", "url": "https://example.invalid/feed", "format": "auto",
            "http": {"authentication": {"type": "none"}},
            "target": {"siteId": 12345, "externalKeyColumn": "ClassZ", "mode": "upsert"},
            "mapping": [{"target": "Title", "source": "title", "default": "無題"}]
          }]
        }
        """;

    private static FeedIngestConfiguration ParseHttp(string httpFragment)
    {
        var json = JsonNode.Parse(Valid)!;
        json["sources"]![0]!["http"] = JsonNode.Parse("{\"authentication\":{\"type\":\"none\"}," + httpFragment + "}");
        return ConfigurationReader.Parse(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void 既定は空で_内部宛を常に拒否する設定になる()
    {
        var http = ConfigurationReader.Parse(Valid).Sources[0].Http;

        Assert.Empty(http.AllowedNetworks);
        Assert.Empty(http.AllowedHosts);
    }

    [Fact]
    public void 許可の範囲とホストを読み取る_ホストは小文字のASCIIへ正規化する()
    {
        var http = ParseHttp("""
            "allowedNetworks": ["10.20.0.0/16", "192.168.1.5/32", "fd00::/8", "127.0.0.1/32"],
            "allowedHosts": ["Feeds.Corp.Example", "10.20.0.5", "::1"]
            """).Sources[0].Http;

        Assert.Equal(4, http.AllowedNetworks.Count);
        Assert.Equal(["feeds.corp.example", "10.20.0.5", "::1"], http.AllowedHosts);
    }

    [Theory]
    [InlineData("10.0.0.0")]            // プレフィックスなし
    [InlineData("10.0.0.1/8")]          // ホスト部が 0 でない
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("10.0.0.0/")]
    [InlineData("/8")]
    [InlineData("example.test/8")]
    [InlineData("fd00::/129")]
    [InlineData("::ffff:10.0.0.0/104")] // IPv4 射影
    [InlineData("0.0.0.0/0")]           // 全許可
    [InlineData("::/0")]
    [InlineData("10.0.0.0/7")]          // 広すぎる
    [InlineData("128.0.0.0/1")]
    [InlineData("fc00::/6")]
    [InlineData("169.254.169.254/32")]  // メタデータ
    [InlineData("169.254.0.0/16")]
    [InlineData("169.254.1.0/24")]
    [InlineData("100.100.100.200/32")]
    [InlineData("0.0.0.0/8")]
    [InlineData("224.0.0.0/4")]
    [InlineData("255.255.255.255/32")]
    [InlineData("fe80::/10")]
    [InlineData("fd00:ec2::254/128")]
    [InlineData("ff02::/16")]
    [InlineData("2002::/16")]
    [InlineData("64:ff9b::/96")]
    [InlineData("::/128")]
    public void 不正な範囲と全許可と常に拒否の範囲は設定の誤りになる(string network)
    {
        var json = JsonNode.Parse(Valid)!;
        json["sources"]![0]!["http"] = JsonNode.Parse(
            "{\"authentication\":{\"type\":\"none\"},\"allowedNetworks\":[" + JsonSerializer.Serialize(network) + "]}");

        var error = Assert.Throws<ConfigurationException>(() =>
            ConfigurationReader.Parse(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));

        Assert.Equal("$.sources[0].http.allowedNetworks[0]", error.Key);
        Assert.True(error.LineNumber > 1);
        Assert.DoesNotContain(network, error.Message);
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("10.20.30.40/32")]
    [InlineData("172.16.0.0/12")]
    [InlineData("192.168.0.0/16")]
    [InlineData("127.0.0.0/8")]
    [InlineData("fc00::/7")]
    [InlineData("fd12:3456:789a::/48")]
    [InlineData("::1/128")]
    [InlineData("100.64.0.0/10")]       // 100.100.100.200 を含むが、そこだけ実行時に拒否する
    public void 妥当な範囲は受け入れる(string network)
    {
        var http = ParseHttp("\"allowedNetworks\": [" + JsonSerializer.Serialize(network) + "]").Sources[0].Http;

        Assert.Single(http.AllowedNetworks);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://feeds.corp.example")]
    [InlineData("feeds.corp.example:8443")]
    [InlineData("feeds.corp.example/path")]
    [InlineData("*.corp.example")]
    [InlineData("feeds.corp.example.")]
    [InlineData(".corp.example")]
    [InlineData("a..b")]
    [InlineData("-a.example")]
    [InlineData("user@feeds.corp.example")]
    [InlineData("feeds corp.example")]
    [InlineData("[::1]")]
    [InlineData("fe80::1%eth0")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("10.0.0.0/8")]
    public void 不正なホストは設定の誤りになる(string host)
    {
        var json = JsonNode.Parse(Valid)!;
        json["sources"]![0]!["http"] = JsonNode.Parse(
            "{\"authentication\":{\"type\":\"none\"},\"allowedHosts\":[" + JsonSerializer.Serialize(host) + "]}");

        var error = Assert.Throws<ConfigurationException>(() =>
            ConfigurationReader.Parse(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));

        Assert.Equal("$.sources[0].http.allowedHosts[0]", error.Key);
    }

    [Theory]
    [InlineData("\"allowedNetworks\": [\"10.0.0.0/8\", \"10.0.0.0/8\"]", "$.sources[0].http.allowedNetworks[1]")]
    [InlineData("\"allowedHosts\": [\"a.example\", \"A.example\"]", "$.sources[0].http.allowedHosts[1]")]
    [InlineData("\"allowedNetworks\": \"10.0.0.0/8\"", "$.sources[0].http.allowedNetworks")]
    [InlineData("\"allowedHosts\": null", "$.sources[0].http.allowedHosts")]
    [InlineData("\"allowedNetworks\": [10]", "$.sources[0].http.allowedNetworks[0]")]
    public void 重複と型の誤りは行とキー付きで拒否する(string fragment, string key)
    {
        var error = Assert.Throws<ConfigurationException>(() => ParseHttp(fragment));

        Assert.Equal(key, error.Key);
    }

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("169.254.0.1", true)]
    [InlineData("100.100.100.200", true)]
    [InlineData("100.100.100.201", false)]
    [InlineData("0.1.2.3", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00:ec2::254", true)]
    [InlineData("ff02::1", true)]
    [InlineData("::", true)]
    [InlineData("2002:7f00:1::1", true)]
    [InlineData("64:ff9b::7f00:1", true)]
    [InlineData("::ffff:169.254.169.254", true)]   // IPv4 射影でも同じ
    [InlineData("10.1.2.3", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("93.184.216.34", false)]
    public void 常に拒否するアドレスの判定(string address, bool never)
    {
        Assert.Equal(never, DestinationRules.IsNeverAllowed(IPAddress.Parse(address)));
    }
}
