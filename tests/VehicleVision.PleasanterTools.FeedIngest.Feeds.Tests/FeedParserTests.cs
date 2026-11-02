using System.Text;
using VehicleVision.PleasanterTools.FeedIngest.Core;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds.Tests;

public sealed class FeedParserTests
{
    private static readonly Uri FeedUrl = new("https://feeds.example.test/dir/news.xml");

    private static FeedParseResult Parse(
        string xml, FeedFormat format = FeedFormat.Auto, string? charset = null, FeedParserLimits? limits = null)
        => FeedParser.Parse(Encoding.UTF8.GetBytes(xml), "src", FeedUrl, format, charset, limits);

    private static string Rss(string items, string rootAttributes = "")
        => $"""<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" {rootAttributes}><channel><title>t</title>{items}</channel></rss>""";

    private static string Atom(string entries, string feedAttributes = "")
        => $"""<?xml version="1.0" encoding="UTF-8"?><feed xmlns="http://www.w3.org/2005/Atom" {feedAttributes}><title>t</title>{entries}</feed>""";

    [Fact]
    public void RSSの全項目を正規化する()
    {
        var result = Parse(Rss("""
            <item>
              <guid isPermaLink="false">Opaque-ID-1</guid>
              <link>/articles/1</link>
              <title>A &amp; B</title>
              <description>&lt;p&gt;要約&lt;b&gt;太字&lt;/b&gt;&lt;/p&gt;</description>
              <content:encoded><![CDATA[<p>本文<script>alert(1)</script></p>]]></content:encoded>
              <pubDate>Thu, 22 Oct 2026 09:00:00 +0900</pubDate>
              <category domain="https://example.test/cat">ニュース</category>
              <category>other</category>
              <author>mail@example.test (山田)</author>
              <dc:creator>Taro</dc:creator>
              <enclosure url="/a.mp3" type="audio/mpeg" length="1234"/>
              <enclosure url="https://cdn.example.test/b.png" type="image/png" length="x"/>
            </item>
            """, "xmlns:content=\"http://purl.org/rss/1.0/modules/content/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\""));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(FeedFormat.Rss20, result.Format);
        Assert.Empty(result.ItemErrors);
        Assert.Equal("src", entry.SourceId);
        Assert.Equal("Opaque-ID-1", entry.NativeId);
        Assert.Equal(EntryIdentityKind.NativeId, entry.IdentityKind);
        Assert.Equal("https://feeds.example.test/articles/1", entry.Link!.AbsoluteUri);
        Assert.Equal("A & B", entry.Title);
        Assert.Equal("要約太字", entry.Summary);
        Assert.Contains("alert(1)", entry.Content, StringComparison.Ordinal);
        Assert.Equal(EntryContentKind.Html, entry.ContentKind);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), entry.PublishedAt);
        Assert.Null(entry.UpdatedAt);
        Assert.Equal(["ニュース", "other"], entry.Categories.Select(c => c.Term));
        Assert.Equal("https://example.test/cat", entry.Categories[0].Scheme);
        Assert.Equal("山田", entry.Authors[0].Name);
        Assert.Equal("mail@example.test", entry.Authors[0].Email);
        Assert.Equal("Taro", entry.Authors[1].Name);
        Assert.Equal(2, entry.Enclosures.Count);
        Assert.Equal("https://feeds.example.test/a.mp3", entry.Enclosures[0].Url.AbsoluteUri);
        Assert.Equal(1234, entry.Enclosures[0].Length);
        Assert.Null(entry.Enclosures[1].Length);
    }

    [Fact]
    public void RSSはguidが無ければlinkを識別子にし両方無ければ項目エラー()
    {
        var result = Parse(Rss("""
            <item><link>https://example.test/x</link><title>x</title></item>
            <item><title>ID無し</title></item>
            <item><guid>ok</guid></item>
            """));

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(EntryIdentityKind.Link, result.Entries[0].IdentityKind);
        Assert.Null(result.Entries[0].NativeId);
        Assert.Equal("ok", result.Entries[1].NativeId);
        var error = Assert.Single(result.ItemErrors);
        Assert.Equal(1, error.Index);
    }

    [Fact]
    public void RSSのguidは不透明文字列のまま大文字小文字を変えない()
    {
        var result = Parse(Rss("<item><guid>  AbC-Ü  </guid></item><item><guid>abc-ü</guid></item>"));

        Assert.Equal(["AbC-Ü", "abc-ü"], result.Entries.Select(e => e.NativeId));
    }

    [Fact]
    public void RSSの不正な日時と日時のオフセット欠落は項目エラーで他は続く()
    {
        var result = Parse(Rss("""
            <item><guid>a</guid><pubDate>Thu, 22 Oct 2026 09:00:00</pubDate></item>
            <item><guid>b</guid><pubDate>not a date</pubDate></item>
            <item><guid>c</guid><pubDate>Thu, 31 Feb 2026 09:00:00 GMT</pubDate></item>
            <item><guid>d</guid><pubDate>22 Oct 26 09:00 EST</pubDate></item>
            """));

        Assert.Equal([0, 1, 2], result.ItemErrors.Select(e => e.Index));
        var entry = Assert.Single(result.Entries);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero), entry.PublishedAt);
    }

    [Fact]
    public void RSSの日時が無ければ現在時刻で補わずnullにする()
    {
        var entry = Assert.Single(Parse(Rss("<item><guid>a</guid></item>")).Entries);

        Assert.Null(entry.PublishedAt);
        Assert.Null(entry.UpdatedAt);
    }

    [Fact]
    public void RSSのcontentencodedが無ければdescriptionを本文にする()
    {
        var entry = Assert.Single(Parse(Rss("<item><guid>a</guid><description>d &amp;amp; e</description></item>")).Entries);

        Assert.Equal("d &amp; e", entry.Content);
        Assert.Equal("d & e", entry.Summary);
    }

    [Fact]
    public void Atomの全項目を正規化する()
    {
        var result = Parse(Atom("""
            <author><name>Feed Author</name></author>
            <entry xml:base="https://base.example.test/root/">
              <id>urn:uuid:ABC</id>
              <title type="html">&lt;i&gt;題&lt;/i&gt;</title>
              <link rel="alternate" type="text/plain" href="plain"/>
              <link href="page.html" type="text/html"/>
              <link rel="enclosure" href="media/a.mp3" type="audio/mpeg" length="99"/>
              <link rel="self" href="self"/>
              <published>2026-10-22T09:00:00+09:00</published>
              <updated>2026-10-23T00:00:00Z</updated>
              <summary>sum &amp; text</summary>
              <content type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>x<b>y</b></p></div></content>
              <category term="t" scheme="https://s.example.test" label="L"/>
              <author><name>Ann</name><email>a@example.test</email><uri>https://ann.example.test</uri></author>
            </entry>
            """));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(FeedFormat.Atom10, result.Format);
        Assert.Equal("urn:uuid:ABC", entry.NativeId);
        Assert.Equal(EntryIdentityKind.NativeId, entry.IdentityKind);
        Assert.Equal("題", entry.Title);
        Assert.Equal("https://base.example.test/root/page.html", entry.Link!.AbsoluteUri);
        Assert.Equal("sum & text", entry.Summary);
        Assert.Equal(EntryContentKind.Xhtml, entry.ContentKind);
        Assert.Equal("xy", HtmlText.ToPlainText(entry.Content));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), entry.PublishedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero), entry.UpdatedAt);
        var category = Assert.Single(entry.Categories);
        Assert.Equal(("t", "https://s.example.test", "L"), (category.Term, category.Scheme, category.Label));
        var author = Assert.Single(entry.Authors);
        Assert.Equal(("Ann", "a@example.test"), (author.Name, author.Email));
        Assert.Equal("https://ann.example.test/", author.Uri!.AbsoluteUri);
        var enclosure = Assert.Single(entry.Enclosures);
        Assert.Equal("https://base.example.test/root/media/a.mp3", enclosure.Url.AbsoluteUri);
        Assert.Equal(99, enclosure.Length);
    }

    [Fact]
    public void Atomのentryにauthorが無ければfeedのauthorを使う()
    {
        var result = Parse(Atom("<author><name>Feed</name></author><entry><id>a</id></entry>"));

        Assert.Equal("Feed", Assert.Single(Assert.Single(result.Entries).Authors).Name);
    }

    [Fact]
    public void Atomのid欠落と日時のオフセット欠落は項目エラー()
    {
        var result = Parse(Atom("""
            <entry><title>no id</title></entry>
            <entry><id>b</id><updated>2026-10-22T09:00:00</updated></entry>
            <entry><id>c</id></entry>
            """));

        Assert.Equal([0, 1], result.ItemErrors.Select(e => e.Index));
        Assert.Equal("c", Assert.Single(result.Entries).NativeId);
    }

    [Fact]
    public void Atomの外部contentと非テキストcontentは展開しない()
    {
        var result = Parse(Atom("""
            <entry><id>a</id><content src="https://example.test/body.html" type="text/html"/></entry>
            <entry><id>b</id><content type="image/png">aGVsbG8=</content></entry>
            <entry><id>c</id><summary>s</summary></entry>
            """));

        Assert.Equal(EntryContentKind.External, result.Entries[0].ContentKind);
        Assert.Null(result.Entries[0].Content);
        Assert.Equal(EntryContentKind.Binary, result.Entries[1].ContentKind);
        Assert.Null(result.Entries[1].Content);
        Assert.Equal("s", result.Entries[2].Content);
    }

    [Fact]
    public void 同じIDで同じ内容は1件にまとめ内容が違えば競合として除く()
    {
        var result = Parse(Rss("""
            <item><guid>same</guid><title>x</title></item>
            <item><guid>same</guid><title>x</title></item>
            <item><guid>diff</guid><title>1</title></item>
            <item><guid>diff</guid><title>2</title></item>
            <item><guid>other</guid></item>
            """));

        Assert.Equal(["same", "other"], result.Entries.Select(e => e.NativeId));
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("diff", conflict.Identity);
        Assert.Equal(2, conflict.Count);
    }

    [Theory]
    [InlineData("""<?xml version="1.0"?><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"/>""")]
    [InlineData("""<rss version="0.91"><channel/></rss>""")]
    [InlineData("""{"version":"https://jsonfeed.org/version/1.1","items":[]}""")]
    [InlineData("""<feed xmlns="http://www.w3.org/2003/atom"/>""")]
    public void 未対応の形式は明示エラー(string xml)
    {
        var exception = Assert.Throws<FeedParseException>(() => Parse(xml));

        Assert.True(exception.Kind is FeedParseErrorKind.UnsupportedFormat or FeedParseErrorKind.Xml);
    }

    [Fact]
    public void 設定した形式と実際の形式が違えばエラー()
    {
        var exception = Assert.Throws<FeedParseException>(() => Parse(Rss(""), FeedFormat.Atom10));

        Assert.Equal(FeedParseErrorKind.UnsupportedFormat, exception.Kind);
    }

    [Fact]
    public void DTDと外部実体を拒否する()
    {
        var withEntity = """<?xml version="1.0"?><!DOCTYPE rss [<!ENTITY x SYSTEM "file:///etc/passwd">]><rss version="2.0"><channel><item><guid>&x;</guid></item></channel></rss>""";
        var empty = """<!DOCTYPE rss><rss version="2.0"><channel/></rss>""";

        Assert.Equal(FeedParseErrorKind.Xml, Assert.Throws<FeedParseException>(() => Parse(withEntity)).Kind);
        Assert.Equal(FeedParseErrorKind.Xml, Assert.Throws<FeedParseException>(() => Parse(empty)).Kind);
    }

    [Fact]
    public void 不正なXMLはメッセージに本文を含めずエラー()
    {
        var exception = Assert.Throws<FeedParseException>(() => Parse("<rss version=\"2.0\"><channel>SECRET-BODY"));

        Assert.Equal(FeedParseErrorKind.Xml, exception.Kind);
        Assert.DoesNotContain("SECRET", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 容量は上限ちょうどを通し1バイト超過を拒否する()
    {
        var xml = Rss("<item><guid>a</guid></item>");
        var size = Encoding.UTF8.GetByteCount(xml);

        Assert.Single(Parse(xml, limits: new FeedParserLimits(MaxBytes: size)).Entries);
        var exception = Assert.Throws<FeedParseException>(() => Parse(xml, limits: new FeedParserLimits(MaxBytes: size - 1)));
        Assert.Equal(FeedParseErrorKind.LimitExceeded, exception.Kind);
    }

    [Fact]
    public void 記事数は上限ちょうどを通し1件超過を拒否する()
    {
        var xml = Rss("<item><guid>a</guid></item><item><guid>b</guid></item>");

        Assert.Equal(2, Parse(xml, limits: new FeedParserLimits(MaxItems: 2)).Entries.Count);
        Assert.Equal(
            FeedParseErrorKind.LimitExceeded,
            Assert.Throws<FeedParseException>(() => Parse(xml, limits: new FeedParserLimits(MaxItems: 1))).Kind);
    }

    [Fact]
    public void 記事本文は上限ちょうどを通し1バイト超過を項目エラーにする()
    {
        var xml = Rss("<item><guid>a</guid><description>あい</description></item>");

        Assert.Single(Parse(xml, limits: new FeedParserLimits(MaxItemContentBytes: 6)).Entries);
        var result = Parse(xml, limits: new FeedParserLimits(MaxItemContentBytes: 5));
        Assert.Empty(result.Entries);
        Assert.Single(result.ItemErrors);
    }

    [Fact]
    public void 相対URIをxmlbaseと最終取得URLで解決する()
    {
        var result = Parse(Rss("""
            <item><guid>a</guid><link>rel.html</link></item>
            <item xml:base="https://other.example.test/x/"><guid>b</guid><link>../up.html</link></item>
            """));

        Assert.Equal("https://feeds.example.test/dir/rel.html", result.Entries[0].Link!.AbsoluteUri);
        Assert.Equal("https://other.example.test/up.html", result.Entries[1].Link!.AbsoluteUri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    public void http以外のlinkは項目エラー(string link)
    {
        var result = Parse(Rss($"<item><guid>a</guid><link>{link}</link></item>"));

        Assert.Empty(result.Entries);
        Assert.Single(result.ItemErrors);
    }

    [Fact]
    public void 本文のHTMLを実行も取得もせず要約はテキストになる()
    {
        var entry = Assert.Single(Parse(Rss(
            "<item><guid>a</guid><description>&lt;img src=\"https://tracker.example.test/p.gif\"&gt;&lt;script&gt;x()&lt;/script&gt;本文</description></item>")).Entries);

        Assert.Equal("本文", entry.Summary);
    }

    [Fact]
    public void 本文の式は評価しない()
    {
        var entry = Assert.Single(Parse(Rss("<item><guid>a</guid><description>{{ 1+1 }} ${x}</description></item>")).Entries);

        Assert.Equal("{{ 1+1 }} ${x}", entry.Summary);
    }

    // 文字コード

    [Fact]
    public void BOM付きUTF8とUTF16を読める()
    {
        var xml = Rss("<item><guid>日本語</guid></item>");
        var utf8 = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray();
        var utf16le = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml.Replace("UTF-8", "UTF-16", StringComparison.Ordinal))).ToArray();
        var utf16be = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(xml.Replace("UTF-8", "UTF-16", StringComparison.Ordinal))).ToArray();

        foreach (var bytes in new[] { utf8, utf16le, utf16be })
        {
            var result = FeedParser.Parse(bytes, "src", FeedUrl);
            Assert.Equal("日本語", Assert.Single(result.Entries).NativeId);
        }
    }

    [Fact]
    public void BOM無しのUTF16は先頭の並びで判定する()
    {
        var xml = Rss("<item><guid>あ</guid></item>").Replace("UTF-8", "UTF-16", StringComparison.Ordinal);

        Assert.Equal("あ", Assert.Single(FeedParser.Parse(Encoding.Unicode.GetBytes(xml), "s", FeedUrl).Entries).NativeId);
        Assert.Equal("あ", Assert.Single(FeedParser.Parse(Encoding.BigEndianUnicode.GetBytes(xml), "s", FeedUrl).Entries).NativeId);
    }

    [Fact]
    public void 明示charsetが宣言と矛盾すればエラー()
    {
        var exception = Assert.Throws<FeedParseException>(
            () => Parse(Rss("<item><guid>a</guid></item>"), charset: "utf-16"));

        Assert.Equal(FeedParseErrorKind.Encoding, exception.Kind);
    }

    [Theory]
    [InlineData("shift_jis")]
    [InlineData("euc-jp")]
    [InlineData("iso-8859-1")]
    public void 未対応の文字コードはEncodingエラー(string charset)
    {
        Assert.Equal(
            FeedParseErrorKind.Encoding,
            Assert.Throws<FeedParseException>(() => Parse(Rss(""), charset: charset)).Kind);
        var declared = Rss("").Replace("UTF-8", charset, StringComparison.Ordinal);
        Assert.Equal(FeedParseErrorKind.Encoding, Assert.Throws<FeedParseException>(() => Parse(declared)).Kind);
    }

    [Fact]
    public void 不正なUTF8のバイトは置換せずEncodingエラー()
    {
        var bytes = Encoding.UTF8.GetBytes(Rss("<item><guid>a</guid></item>"));
        var broken = bytes.Take(bytes.Length - 20).Concat(new byte[] { 0xFF, 0xFE, 0x80 }).Concat(bytes.Skip(bytes.Length - 20)).ToArray();

        var exception = Assert.Throws<FeedParseException>(() => FeedParser.Parse(broken, "s", FeedUrl));

        Assert.Equal(FeedParseErrorKind.Encoding, exception.Kind);
    }

    [Fact]
    public void BOMが明示charsetに優先する()
    {
        var xml = Rss("<item><guid>a</guid></item>");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray();

        Assert.Single(FeedParser.Parse(bytes, "s", FeedUrl, FeedFormat.Auto, "utf-8").Entries);
    }

    [Fact]
    public void 宣言もcharsetも無ければUTF8とみなす()
    {
        var bytes = Encoding.UTF8.GetBytes("""<rss version="2.0"><channel><item><guid>日</guid></item></channel></rss>""");

        Assert.Equal("日", Assert.Single(FeedParser.Parse(bytes, "s", FeedUrl).Entries).NativeId);
    }
}
