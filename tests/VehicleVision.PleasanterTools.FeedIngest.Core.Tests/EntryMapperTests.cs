using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
using VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Tests;

public sealed class EntryMapperTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private static string Config(string mapping) => $$$"""
        {
          "schemaVersion": 1,
          "pleasanter": {"id": "office", "baseUrl": "https://example.invalid", "apiVersion": 1.1,
            "apiKeyEnv": "TEST_API_KEY", "apiKeyUserTimeZoneId": "Asia/Tokyo"},
          "sources": [{
            "id": "vendor-news", "url": "https://example.invalid/feed", "format": "auto",
            "http": {"authentication": {"type": "none"}},
            "target": {"siteId": 12345, "externalKeyColumn": "ClassZ", "mode": "upsert"},
            "mapping": [{{{mapping}}}]
          }]
        }
        """;

    private static EntryMapper Mapper(string mapping)
    {
        var config = ConfigurationReader.Parse(Config(mapping));
        return new EntryMapper(config.Pleasanter, config.Sources[0]);
    }

    private static NormalizedEntry Entry(
        string? title = "題名", string? content = "<p>本文</p>", EntryContentKind kind = EntryContentKind.Html,
        DateTimeOffset? published = null, IEnumerable<EntryCategory>? categories = null,
        IEnumerable<EntryEnclosure>? enclosures = null, string? summary = "要約")
        => new("vendor-news", "id-1", EntryIdentityKind.NativeId, new Uri("https://news.example.test/a"),
            title, summary, content, kind, published, null, categories, null, enclosures);

    private static MappedRecord Success(MappingResult result)
    {
        Assert.True(result.IsSuccess, string.Join(",", result.Errors.Select(e => e.Target + e.Reason)));
        return result.Record!;
    }

    [Fact]
    public void 設定のとおり固定値と元項目と変換を列へ対応づける()
    {
        var mapper = Mapper("""
            {"target": "Title", "source": "title", "default": "（無題）"},
            {"target": "Body", "source": "contentText"},
            {"target": "ClassA", "source": "link"},
            {"target": "ClassB", "source": "categories", "transform": "join", "separator": ", "},
            {"target": "ClassC", "constant": "vendor-news"},
            {"target": "DateA", "source": "publishedAt"},
            {"target": "DescriptionA", "source": "summaryText"},
            {"target": "DescriptionB", "source": "enclosures", "transform": "json"},
            {"target": "NumA", "constant": 0},
            {"target": "CheckA", "constant": false}
            """);
        var entry = Entry(
            published: new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
            categories: [new EntryCategory("a"), new EntryCategory("b", Label: "ラベル")],
            enclosures: [new EntryEnclosure(new Uri("https://cdn.example.test/a.mp3"), "audio/mpeg", 12), new EntryEnclosure(new Uri("https://cdn.example.test/b"))]);

        var record = Success(mapper.Map(entry));
        var create = record.ToCreatePayload();

        Assert.Equal("題名", create["Title"]);
        Assert.Equal("本文", create["Body"]);
        var classes = Assert.IsType<Dictionary<string, object?>>(create["ClassHash"]);
        Assert.Equal("https://news.example.test/a", classes["ClassA"]);
        Assert.Equal("a, ラベル", classes["ClassB"]);
        Assert.Equal("vendor-news", classes["ClassC"]);
        Assert.Equal(ExternalKey.For(entry), classes["ClassZ"]);
        var dates = Assert.IsType<Dictionary<string, object?>>(create["DateHash"]);
        Assert.Equal("2026-10-22T09:00:00", dates["DateA"]);
        var descriptions = Assert.IsType<Dictionary<string, object?>>(create["DescriptionHash"]);
        Assert.Equal("要約", descriptions["DescriptionA"]);
        Assert.Equal(
            """[{"url":"https://cdn.example.test/a.mp3","type":"audio/mpeg","length":12},{"url":"https://cdn.example.test/b","type":null,"length":null}]""",
            descriptions["DescriptionB"]);
        Assert.Equal(0m, Assert.IsType<Dictionary<string, object?>>(create["NumHash"])["NumA"]);
        Assert.Equal(false, Assert.IsType<Dictionary<string, object?>>(create["CheckHash"])["CheckA"]);
    }

    [Fact]
    public void 更新の本文は所有列だけで外部キー列を含まない()
    {
        var record = Success(Mapper("""{"target": "Title", "source": "title"}, {"target": "ClassA", "source": "link"}""").Map(Entry()));

        var update = record.ToUpdatePayload();

        Assert.Equal("題名", update["Title"]);
        Assert.DoesNotContain("ClassZ", Assert.IsType<Dictionary<string, object?>>(update["ClassHash"]).Keys);
        Assert.DoesNotContain("ApiKey", update.Keys);
    }

    [Fact]
    public void 欠落は既定でその列に触らず既定値があれば使う()
    {
        var mapper = Mapper("""
            {"target": "Title", "source": "title", "default": "（無題）"},
            {"target": "Body", "source": "contentText", "onMissing": "omit"},
            {"target": "ClassA", "source": "summaryText"}
            """);

        var record = Success(mapper.Map(Entry(title: "   ", content: null, summary: "")));

        Assert.Equal("（無題）", record.Columns["Title"]);
        Assert.DoesNotContain("Body", record.Columns.Keys);
        Assert.DoesNotContain("ClassA", record.Columns.Keys);
    }

    [Fact]
    public void 欠落が必須ならその記事全体をエラーにする()
    {
        var mapper = Mapper("""
            {"target": "Title", "source": "title"},
            {"target": "Body", "source": "contentText", "onMissing": "error"}
            """);

        var result = mapper.Map(Entry(content: null));

        Assert.False(result.IsSuccess);
        Assert.Equal("Body", Assert.Single(result.Errors).Target);
    }

    [Fact]
    public void 空文字の固定値は列のクリアとして残し0とfalseも有効値として保持する()
    {
        var record = Success(Mapper("""
            {"target": "ClassA", "constant": ""},
            {"target": "NumA", "constant": 0},
            {"target": "CheckA", "constant": false}
            """).Map(Entry()));

        Assert.Equal(string.Empty, record.Columns["ClassA"]);
        Assert.Equal(0m, record.Columns["NumA"]);
        Assert.Equal(false, record.Columns["CheckA"]);
    }

    [Fact]
    public void joinは空要素を除き元の順序を保つ()
    {
        var mapper = Mapper("""{"target": "ClassA", "source": "categories", "transform": "join", "separator": "/"}""");

        var record = Success(mapper.Map(Entry(categories: [new EntryCategory("b"), new EntryCategory("c"), new EntryCategory("a")])));

        Assert.Equal("b/c/a", record.Columns["ClassA"]);
        Assert.DoesNotContain("ClassA", Success(mapper.Map(Entry())).Columns.Keys);
    }

    [Fact]
    public void 本文のテキスト化は種類で分け外部とバイナリは欠落として扱う()
    {
        var mapper = Mapper("""{"target": "Body", "source": "contentText"}""");

        Assert.Equal("a < b", Success(mapper.Map(Entry(content: "a < b", kind: EntryContentKind.Text))).Columns["Body"]);
        Assert.Equal("x", Success(mapper.Map(Entry(content: "<script>y()</script><b>x</b>", kind: EntryContentKind.Html))).Columns["Body"]);
        Assert.DoesNotContain("Body", Success(mapper.Map(Entry(content: null, kind: EntryContentKind.External))).Columns.Keys);
        Assert.DoesNotContain("Body", Success(mapper.Map(Entry(content: null, kind: EntryContentKind.Binary))).Columns.Keys);
    }

    [Fact]
    public void 本文の式は評価しない()
    {
        var record = Success(Mapper("""{"target": "Body", "source": "contentText"}""")
            .Map(Entry(content: "{{ 1+1 }} ${x}", kind: EntryContentKind.Text)));

        Assert.Equal("{{ 1+1 }} ${x}", record.Columns["Body"]);
    }

    [Fact]
    public void 日時は利用者のタイムゾーンへ変換し日時の既定値も同じ規則で変換する()
    {
        var mapper = Mapper("""
            {"target": "DateA", "source": "publishedAt"},
            {"target": "DateB", "source": "updatedAt", "default": "2026-01-01T00:00:00Z"}
            """);

        var record = Success(mapper.Map(Entry(published: new DateTimeOffset(2026, 10, 22, 20, 30, 15, TimeSpan.FromHours(-5)))));

        Assert.Equal("2026-10-23T10:30:15", record.Columns["DateA"]);
        Assert.Equal("2026-01-01T09:00:00", record.Columns["DateB"]);
        Assert.DoesNotContain("DateA", Success(mapper.Map(Entry())).Columns.Keys);
    }

    [Fact]
    public void 日時が無ければ現在時刻で補わない()
    {
        var record = Success(Mapper("""{"target": "DateA", "source": "publishedAt"}""").Map(Entry()));

        Assert.Empty(record.Columns);
    }

    [Fact]
    public void 同じ入力と設定なら常に同じ値とハッシュで違えば変わる()
    {
        var mapper = Mapper("""{"target": "Title", "source": "title"}, {"target": "NumA", "constant": 1.0}""");
        var same = Mapper("""{"target": "Title", "source": "title"}, {"target": "NumA", "constant": 1.0}""");
        var otherVersion = Mapper("""{"target": "Title", "source": "title"}, {"target": "NumA", "constant": 2}""");

        var first = Success(mapper.Map(Entry()));
        var second = Success(same.Map(Entry()));

        Assert.Equal(first.ValueHash, second.ValueHash);
        Assert.Equal(mapper.Version, same.Version);
        Assert.NotEqual(mapper.Version, otherVersion.Version);
        Assert.NotEqual(first.ValueHash, Success(mapper.Map(Entry(title: "別"))).ValueHash);
        Assert.Equal(64, first.ValueHash.Length);
    }

    [Fact]
    public void 数値の表記が違っても同じ値なら同じハッシュ()
    {
        var a = Success(Mapper("""{"target": "NumA", "constant": 1.0}""").Map(Entry()));
        var b = Success(Mapper("""{"target": "NumA", "constant": 1}""").Map(Entry()));

        Assert.Equal(a.ValueHash, b.ValueHash);
    }

    [Fact]
    public void 切り詰めをしない()
    {
        var longTitle = new string('あ', 5000);

        var record = Success(Mapper("""{"target": "Title", "source": "title"}""").Map(Entry(title: longTitle)));

        Assert.Equal(longTitle, record.Columns["Title"]);
    }

    // 外部キー

    [Fact]
    public void 外部キーは固定のベクトルと一致する()
    {
        // 値は Python の hashlib と json.dumps(separators=(",", ":"), ensure_ascii=False) で独立に計算した
        Assert.Equal(
            "fi:v1:c5f05ae0b84cd042a8096e9fe6e97f73503b4d9e0a67fdcc1c3ddd2ced368340",
            ExternalKey.Compute("vendor-news", EntryIdentityKind.NativeId, "Opaque-ID-1"));
        Assert.Equal(
            "fi:v1:1f0351b1fb0c29612d33a90db217d5973aea6a3f45abf5276b31ba4c5884fcfe",
            ExternalKey.Compute("vendor-news", EntryIdentityKind.NativeId, "日本語\"\\\n\u0001"));
        Assert.Equal(
            "fi:v1:5bfd23b6079cc9ccef22ad0868501503f449a63cc5f2dba420213327db66b87f",
            ExternalKey.Compute("s", EntryIdentityKind.Link, "https://news.example.test/items/42?a=1&b=2"));
    }

    [Fact]
    public void 外部キーは境界が衝突せず種類と購読元で変わり大文字小文字を区別する()
    {
        var keys = new[]
        {
            ExternalKey.Compute("a", EntryIdentityKind.NativeId, "bc"),
            ExternalKey.Compute("ab", EntryIdentityKind.NativeId, "c"),
            ExternalKey.Compute("a", EntryIdentityKind.Link, "bc"),
            ExternalKey.Compute("a", EntryIdentityKind.NativeId, "BC"),
            ExternalKey.Compute("a", EntryIdentityKind.NativeId, "bc "),
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.All(keys, key =>
        {
            Assert.StartsWith("fi:v1:", key, StringComparison.Ordinal);
            Assert.Equal(6 + 64, key.Length);
            Assert.Equal(key.ToLowerInvariant(), key);
        });
    }

    [Fact]
    public void linkの外部キーはschemeとhostと既定ポートだけを正規化しqueryとpathを保つ()
    {
        string KeyOf(string url) => ExternalKey.For(new NormalizedEntry(
            "s", null, EntryIdentityKind.Link, new Uri(url), null, null, null, EntryContentKind.Text));

        Assert.Equal(KeyOf("https://news.example.test/a?x=1"), KeyOf("HTTPS://News.Example.TEST:443/a?x=1"));
        Assert.NotEqual(KeyOf("https://news.example.test/a?x=1"), KeyOf("https://news.example.test/a?x=2"));
        Assert.NotEqual(KeyOf("https://news.example.test/a"), KeyOf("https://news.example.test/A"));
        Assert.NotEqual(KeyOf("https://news.example.test/a"), KeyOf("https://news.example.test:8443/a"));
    }

    [Fact]
    public void linkの外部キーはUriの正規形でfragmentを含まずdot_segmentを解決する()
    {
        string KeyOf(string url) => ExternalKey.For(new NormalizedEntry(
            "s", null, EntryIdentityKind.Link, new Uri(url), null, null, null, EntryContentKind.Text));

        Assert.Equal(KeyOf("https://news.example.test/a/b?x=1"), KeyOf("https://news.example.test/a/b?x=1#section"));
        Assert.Equal(KeyOf("https://news.example.test/a/b"), KeyOf("https://news.example.test/a/c/../b"));
        Assert.Equal(KeyOf("https://news.example.test/a/b"), KeyOf("https://news.example.test/a/./b"));
        Assert.Equal(
            "https://news.example.test/a b".Replace(" ", "%20", StringComparison.Ordinal),
            new Uri("https://news.example.test/a b").GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped));
        Assert.Equal(
            "https://news.example.test/a%20b?x=1",
            ExternalKey.IdentityOf(new NormalizedEntry(
                "s", null, EntryIdentityKind.Link, new Uri("HTTPS://News.Example.TEST:443/a%20b?x=1#f"), null, null, null, EntryContentKind.Text)));
    }

    [Fact]
    public void 元IDの外部キーはリンクの変更で変わらない()
    {
        NormalizedEntry With(string link) => new("s", "guid-1", EntryIdentityKind.NativeId, new Uri(link), null, null, null, EntryContentKind.Text);

        Assert.Equal(ExternalKey.For(With("https://a.example.test/1")), ExternalKey.For(With("https://b.example.test/2")));
    }

    [Fact]
    public void 外部キーの引数を検査する()
    {
        Assert.Throws<ArgumentException>(() => ExternalKey.Compute(" ", EntryIdentityKind.NativeId, "x"));
        Assert.Throws<ArgumentException>(() => ExternalKey.Compute("s", EntryIdentityKind.NativeId, ""));
        Assert.Throws<ArgumentException>(() => ExternalKey.Compute("s", (EntryIdentityKind)99, "x"));
    }
}
