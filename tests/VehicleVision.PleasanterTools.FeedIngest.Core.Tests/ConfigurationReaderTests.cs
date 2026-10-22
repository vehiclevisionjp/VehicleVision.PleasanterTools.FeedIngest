using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Tests;

public sealed class ConfigurationReaderTests
{
    private const string Valid = """
        {
          "schemaVersion": 1,
          "pleasanter": {
            "id": "office",
            "baseUrl": "https://example.invalid",
            "apiVersion": 1.1,
            "apiKeyEnv": "TEST_API_KEY",
            "apiKeyUserTimeZoneId": "Asia/Tokyo"
          },
          "sources": [{
            "id": "news",
            "url": "https://example.invalid/feed",
            "format": "auto",
            "http": {"authentication": {"type": "none"}},
            "target": {"siteId": 12345, "externalKeyColumn": "ClassZ", "mode": "upsert"},
            "mapping": [{"target": "Title", "source": "title", "default": "無題"}]
          }]
        }
        """;

    [Fact]
    public void 検証済み設定は型と既定値を持ち秘密を解決しない()
    {
        var config = ConfigurationReader.Parse(Valid);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal("TEST_API_KEY", config.Pleasanter.ApiKey.EnvironmentVariable);
        var source = Assert.Single(config.Sources);
        Assert.Equal(FeedFormat.Auto, source.Format);
        Assert.Equal(30, source.Http.TimeoutSeconds);
        Assert.Equal(5, source.Http.MaxRedirects);
        Assert.Equal(10485760, source.Http.MaxBodyBytes);
        Assert.Equal(MissingValueBehavior.Omit, Assert.Single(source.Mapping).OnMissing);
    }

    [Fact]
    public void 文書の完全な設定例を読み取れる()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "mapping.md"));
        var json = text.Split("```json", StringSplitOptions.None)[1].Split("```", StringSplitOptions.None)[0];
        var config = ConfigurationReader.Parse(json);
        Assert.Equal(9, Assert.Single(config.Sources).Mapping.Count);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("schemaVersion", "\"1\"")]
    [InlineData("schemaVersion", "null")]
    [InlineData("pleasanter.apiVersion", "1.2")]
    [InlineData("pleasanter.baseUrl", "\"http://example.invalid\"")]
    [InlineData("pleasanter.baseUrl", "\"https://user:password@example.invalid\"")]
    [InlineData("pleasanter.baseUrl", "\"https://example.invalid/#fragment\"")]
    [InlineData("pleasanter.apiKeyEnv", "\"TOKEN_FILE\"")]
    [InlineData("pleasanter.apiKeyEnv", "\"not a name\"")]
    [InlineData("pleasanter.apiKeyUserTimeZoneId", "\"unknown-zone\"")]
    [InlineData("sources", "[]")]
    [InlineData("sources", "{}")]
    [InlineData("sources.0.id", "\" \"")]
    [InlineData("sources.0.url", "\"http://example.invalid\"")]
    [InlineData("sources.0.format", "\"rss10\"")]
    [InlineData("sources.0.http.timeoutSeconds", "0")]
    [InlineData("sources.0.http.timeoutSeconds", "31")]
    [InlineData("sources.0.http.maxRedirects", "-1")]
    [InlineData("sources.0.http.maxRedirects", "6")]
    [InlineData("sources.0.http.maxBodyBytes", "0")]
    [InlineData("sources.0.http.maxBodyBytes", "10485761")]
    [InlineData("sources.0.target.siteId", "0")]
    [InlineData("sources.0.target.siteId", "1.5")]
    [InlineData("sources.0.target.siteId", "9223372036854775808")]
    [InlineData("sources.0.target.mode", "\"overwrite\"")]
    [InlineData("sources.0.target.externalKeyColumn", "\"Title\"")]
    [InlineData("sources.0.mapping", "[]")]
    [InlineData("sources.0.mapping.0.target", "\"ClassZ\"")]
    [InlineData("sources.0.mapping.0.target", "\"AttachmentsA\"")]
    [InlineData("sources.0.mapping.0.target", "\"Creator\"")]
    [InlineData("sources.0.mapping.0.target", "\"ClassAA\"")]
    [InlineData("sources.0.mapping.0.source", "\"unknown\"")]
    [InlineData("sources.0.mapping.0.source", "\"categories\"")]
    [InlineData("sources.0.mapping.0.source", "\"enclosures\"")]
    [InlineData("sources.0.mapping.0.source", "\"publishedAt\"")]
    [InlineData("sources.0.mapping.0.transform", "\"script\"")]
    [InlineData("sources.0.mapping.0.transform", "\"join\"")]
    [InlineData("sources.0.mapping.0.onMissing", "\"ignore\"")]
    [InlineData("sources.0.mapping.0.default", "0")]
    [InlineData("sources.0.mapping.0.template", "\"{{title}}\"")]
    [InlineData("pleasanter.apiKey", "\"DO_NOT_ECHO_THIS\"")]
    public void 不正な型範囲と未知項目を行とキー付きで拒否する(string path, string value)
    {
        var json = JsonNode.Parse(Valid)!;
        Set(json, path, JsonNode.Parse(value));
        var error = Assert.Throws<ConfigurationException>(() =>
            ConfigurationReader.Parse(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        Assert.True(error.LineNumber > 0);
        Assert.StartsWith("$", error.Key);
        Assert.DoesNotContain("DO_NOT_ECHO_THIS", error.ToString());
    }

    [Theory]
    [InlineData("Title", """{"source":"title"}""")]
    [InlineData("Class001", """{"source":"sourceId"}""")]
    [InlineData("ClassA", """{"source":"categories","transform":"join","separator":", "}""")]
    [InlineData("DescriptionA", """{"source":"enclosures","transform":"json"}""")]
    [InlineData("DateA", """{"source":"publishedAt","onMissing":"omit"}""")]
    [InlineData("NumA", """{"constant":0}""")]
    [InlineData("CheckA", """{"constant":false}""")]
    [InlineData("Body", """{"constant":""}""")]
    [InlineData("DateA", """{"constant":"2026-10-22T09:00:00+09:00"}""")]
    public void 宣言された型の対応づけを受け入れる(string target, string value)
    {
        var json = JsonNode.Parse(Valid)!;
        var rule = JsonNode.Parse(value)!;
        rule["target"] = target;
        Set(json, "sources.0.mapping.0", rule);
        var config = ConfigurationReader.Parse(json.ToJsonString());
        Assert.Equal(target, Assert.Single(Assert.Single(config.Sources).Mapping).Target);
    }

    [Theory]
    [InlineData("""{"target":"Title","constant":"a","source":"title"}""")]
    [InlineData("""{"target":"Title","constant":"a","default":"b"}""")]
    [InlineData("""{"target":"Title"}""")]
    [InlineData("""{"target":"NumA","constant":"1"}""")]
    [InlineData("""{"target":"CheckA","constant":"false"}""")]
    [InlineData("""{"target":"DateA","constant":""}""")]
    [InlineData("""{"target":"DateA","constant":"2026-10-22T09:00:00"}""")]
    [InlineData("""{"target":"DateA","constant":"2026-02-30T00:00:00Z"}""")]
    [InlineData("""{"target":"Title","constant":null}""")]
    [InlineData("""{"target":"Title","constant":[] }""")]
    [InlineData("""{"target":"NumA","source":"title"}""")]
    [InlineData("""{"target":"ClassA","source":"title","separator":","}""")]
    public void 不整合な対応づけを拒否する(string rule)
    {
        var json = JsonNode.Parse(Valid)!;
        Set(json, "sources.0.mapping.0", JsonNode.Parse(rule));
        Assert.Throws<ConfigurationException>(() => ConfigurationReader.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData("""{"type":"basic","usernameEnv":"USER","passwordEnv":"PASSWORD"}""")]
    [InlineData("""{"type":"headers","headers":[{"name":"Authorization","valueEnv":"AUTH"}]}""")]
    [InlineData("""{"type":"headers","headers":[{"name":"X-Token","valueEnv":"TOKEN"}]}""")]
    public void 認証の秘密参照を受け入れる(string auth)
    {
        var json = JsonNode.Parse(Valid)!;
        Set(json, "sources.0.http.authentication", JsonNode.Parse(auth));
        _ = ConfigurationReader.Parse(json.ToJsonString());
    }

    [Theory]
    [InlineData("""{"type":"none","usernameEnv":"USER"}""")]
    [InlineData("""{"type":"basic","usernameEnv":"USER"}""")]
    [InlineData("""{"type":"basic","usernameEnv":"USER","passwordEnv":"PASS","headers":[]}""")]
    [InlineData("""{"type":"headers","headers":[]}""")]
    [InlineData("""{"type":"headers","headers":[{"name":"Host","valueEnv":"AUTH"}]}""")]
    [InlineData("""{"type":"headers","headers":[{"name":"If-None-Match","valueEnv":"AUTH"}]}""")]
    [InlineData("""{"type":"headers","headers":[{"name":"Content-Length","valueEnv":"AUTH"}]}""")]
    [InlineData("""{"type":"headers","headers":[{"name":"X\r\nBad","valueEnv":"AUTH"}]}""")]
    [InlineData("""{"type":"headers","headers":[{"name":"X","valueEnv":"A"},{"name":"x","valueEnv":"B"}]}""")]
    public void 認証混在と禁止ヘッダを拒否する(string auth)
    {
        var json = JsonNode.Parse(Valid)!;
        Set(json, "sources.0.http.authentication", JsonNode.Parse(auth));
        Assert.Throws<ConfigurationException>(() => ConfigurationReader.Parse(json.ToJsonString()));
    }

    [Fact]
    public void 重複プロパティは正確な行とキーを示す()
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            ConfigurationReader.Parse("{\n\"schemaVersion\":1,\n\"schemaVersion\":1\n}"));
        Assert.Equal(3, error.LineNumber);
        Assert.Equal("$.schemaVersion", error.Key);
    }

    [Fact]
    public void 入れ子の重複とUnicodeの前後でも行を正確に示す()
    {
        var json = Valid.Replace("\"format\": \"auto\",", "\"format\": \"auto\",\n\"format\": \"rss20\",",
            StringComparison.Ordinal);
        var error = Assert.Throws<ConfigurationException>(() => ConfigurationReader.Parse(json));
        var duplicateLine = json[..json.LastIndexOf("\"format\"", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
        Assert.Equal(duplicateLine, error.LineNumber);
        Assert.Equal("$.sources[0].format", error.Key);
        var wrong = Valid.Replace("\"default\": \"無題\"", "\"default\": \"無題\",\n\"script\": \"secret-value\"",
            StringComparison.Ordinal);
        error = Assert.Throws<ConfigurationException>(() => ConfigurationReader.Parse(wrong));
        Assert.Equal("$.sources[0].mapping[0].script", error.Key);
        Assert.DoesNotContain("secret-value", error.ToString());
    }

    [Fact]
    public void sourceと列の重複を拒否する()
    {
        var json = JsonNode.Parse(Valid)!;
        var sources = json["sources"]!.AsArray();
        sources.Add(sources[0]!.DeepClone());
        Assert.Throws<ConfigurationException>(() => ConfigurationReader.Parse(json.ToJsonString()));
        sources.RemoveAt(1);
        var mapping = sources[0]!["mapping"]!.AsArray();
        mapping.Add(mapping[0]!.DeepClone());
        Assert.Throws<ConfigurationException>(() => ConfigurationReader.Parse(json.ToJsonString()));
    }

    [Fact]
    public void 必須項目の欠落と不正JSONは値を漏らさない()
    {
        var missing = Assert.Throws<ConfigurationException>(() => ConfigurationReader.Parse("{}"));
        Assert.Equal("$.schemaVersion", missing.Key);
        var invalid = Assert.Throws<ConfigurationException>(() =>
            ConfigurationReader.Parse("{\n\"apiKey\": DO_NOT_ECHO_THIS}"));
        Assert.Equal(2, invalid.LineNumber);
        Assert.DoesNotContain("DO_NOT_ECHO_THIS", invalid.ToString());
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(30, 5, 10485760)]
    public void 範囲の両端を受け入れる(int timeout, int redirects, int bytes)
    {
        var json = JsonNode.Parse(Valid)!;
        Set(json, "sources.0.http.timeoutSeconds", JsonValue.Create(timeout));
        Set(json, "sources.0.http.maxRedirects", JsonValue.Create(redirects));
        Set(json, "sources.0.http.maxBodyBytes", JsonValue.Create(bytes));
        Set(json, "sources.0.target.siteId", JsonValue.Create(long.MaxValue));
        _ = ConfigurationReader.Parse(json.ToJsonString());
    }

    private static void Set(JsonNode root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var node = root;
        foreach (var part in parts[..^1])
        {
            node = node is JsonArray array ? array[int.Parse(part)]! : node[part]!;
        }
        if (node is JsonArray target)
        {
            target[int.Parse(parts[^1])] = value;
        }
        else
        {
            node[parts[^1]] = value;
        }
    }
}
