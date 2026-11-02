using VehicleVision.PleasanterTools.FeedIngest.Core.State;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Tests;

public sealed class EntrySnapshotTests
{
    private static NormalizedEntry Full() => new(
        "src", "Opaque ID", EntryIdentityKind.NativeId, new Uri("https://news.example.test/a?x=1"),
        "題名 \"quoted\"", "要約", "<p>本文 & <b>太字</b></p>", EntryContentKind.Html,
        new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.FromHours(9)),
        new DateTimeOffset(2026, 10, 23, 0, 0, 0, 123, TimeSpan.Zero).AddTicks(4560),
        [new EntryCategory("a", "scheme", "ラベル"), new EntryCategory("b")],
        [new EntryAuthor("Ann", "a@example.test", new Uri("https://ann.example.test")), new EntryAuthor("Bob")],
        [new EntryEnclosure(new Uri("https://cdn.example.test/a.mp3"), "audio/mpeg", 12), new EntryEnclosure(new Uri("https://cdn.example.test/b"))]);

    private static NormalizedEntry Minimal() => new(
        "src", null, EntryIdentityKind.Link, new Uri("https://news.example.test/b"), null, null, null, EntryContentKind.External);

    [Theory]
    [MemberData(nameof(Entries))]
    public void 往復しても同じ内容になる(NormalizedEntry entry)
    {
        var json = EntrySnapshot.ToJson(entry);

        var restored = EntrySnapshot.FromJson(json);

        Assert.Equal(json, EntrySnapshot.ToJson(restored));
        Assert.Equal(entry.NativeId, restored.NativeId);
        Assert.Equal(entry.IdentityKind, restored.IdentityKind);
        Assert.Equal(entry.Link, restored.Link);
        Assert.Equal(entry.Title, restored.Title);
        Assert.Equal(entry.Content, restored.Content);
        Assert.Equal(entry.ContentKind, restored.ContentKind);
        Assert.Equal(entry.PublishedAt, restored.PublishedAt);
        Assert.Equal(entry.UpdatedAt, restored.UpdatedAt);
        Assert.Equal(entry.Categories, restored.Categories);
        Assert.Equal(entry.Authors, restored.Authors);
        Assert.Equal(entry.Enclosures, restored.Enclosures);
    }

    public static TheoryData<NormalizedEntry> Entries => new() { Full(), Minimal() };

    [Fact]
    public void 日時の欠落は欠落のまま現在時刻で補わない()
    {
        var restored = EntrySnapshot.FromJson(EntrySnapshot.ToJson(Minimal()));

        Assert.Null(restored.PublishedAt);
        Assert.Null(restored.UpdatedAt);
        Assert.Null(restored.NativeId);
    }

    [Fact]
    public void 同じ記事は常に同じJSONとハッシュで_違えば変わる()
    {
        var json = EntrySnapshot.ToJson(Full());

        Assert.Equal(json, EntrySnapshot.ToJson(Full()));
        Assert.Equal(EntrySnapshot.HashOf(json), EntrySnapshot.HashOf(EntrySnapshot.ToJson(Full())));
        Assert.Equal(64, EntrySnapshot.HashOf(json).Length);
        Assert.NotEqual(EntrySnapshot.HashOf(json), EntrySnapshot.HashOf(EntrySnapshot.ToJson(Minimal())));
    }

    [Fact]
    public void 日本語を読める形で保存し_未知の版は拒否する()
    {
        var json = EntrySnapshot.ToJson(Full());

        Assert.Contains("題名", json, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => EntrySnapshot.FromJson(json.Replace("\"V\":1", "\"V\":2", StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => EntrySnapshot.FromJson("{not json"));
    }
}
