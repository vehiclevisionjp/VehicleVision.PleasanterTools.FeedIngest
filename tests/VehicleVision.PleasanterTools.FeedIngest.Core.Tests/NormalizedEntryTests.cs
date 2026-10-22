namespace VehicleVision.PleasanterTools.FeedIngest.Core.Tests;

public sealed class NormalizedEntryTests
{
    [Fact]
    public void 記事は元IDとメタデータを保持し日時をUTCへ正規化する()
    {
        var categories = new List<EntryCategory> { new("news", "domain", "News") };
        var entry = new NormalizedEntry("source", "Opaque ID", EntryIdentityKind.NativeId,
            new Uri("https://example.invalid"), "title", "summary", "<p>raw</p>", EntryContentKind.Html,
            new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.FromHours(9)), null,
            categories, [new EntryAuthor("name")], [new EntryEnclosure(new Uri("https://example.invalid/a"), "image/png", 0)]);
        categories.Clear();
        Assert.Equal("Opaque ID", entry.NativeId);
        Assert.Equal(TimeSpan.Zero, entry.PublishedAt!.Value.Offset);
        Assert.Equal(0, entry.PublishedAt.Value.Hour);
        Assert.Null(entry.UpdatedAt);
        Assert.Single(entry.Categories);
        Assert.Single(entry.Authors);
        Assert.Equal(0, Assert.Single(entry.Enclosures).Length);
        Assert.Equal("<p>raw</p>", entry.Content);
    }

    [Fact]
    public void 日時欠落を現在時刻で置換しない()
    {
        var entry = new NormalizedEntry("s", null, EntryIdentityKind.Link, new Uri("https://example.invalid"),
            null, null, null, EntryContentKind.Text);
        Assert.Null(entry.NativeId);
        Assert.Null(entry.PublishedAt);
        Assert.Null(entry.UpdatedAt);
    }

    [Fact]
    public void 識別子欠落と相対URIと負の添付サイズを拒否する()
    {
        Assert.Throws<ArgumentException>(() => new NormalizedEntry("s", null, EntryIdentityKind.NativeId,
            null, null, null, null, EntryContentKind.Text));
        Assert.Throws<ArgumentException>(() => new NormalizedEntry("s", null, EntryIdentityKind.Link,
            null, null, null, null, EntryContentKind.Text));
        Assert.Throws<ArgumentException>(() => new NormalizedEntry("s", "id", EntryIdentityKind.NativeId,
            new Uri("relative", UriKind.Relative), null, null, null, EntryContentKind.Text));
        Assert.Throws<ArgumentException>(() => new NormalizedEntry("s", "id", EntryIdentityKind.NativeId,
            null, null, null, null, EntryContentKind.Text,
            enclosures: [new EntryEnclosure(new Uri("https://example.invalid"), Length: -1)]));
    }
}
