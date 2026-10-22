namespace VehicleVision.PleasanterTools.FeedIngest.Core;

public enum EntryIdentityKind { NativeId, Link }

public enum EntryContentKind { Text, Html, Xhtml, External, Binary }

public sealed record EntryCategory(string Term, string? Scheme = null, string? Label = null);

public sealed record EntryAuthor(string Name, string? Email = null, Uri? Uri = null);

public sealed record EntryEnclosure(Uri Url, string? Type = null, long? Length = null);

/// <summary>解析層が生成する記事。本文は未信頼データであり、実行・取得しない。</summary>
public sealed class NormalizedEntry
{
    public NormalizedEntry(
        string sourceId, string? nativeId, EntryIdentityKind identityKind, Uri? link,
        string? title, string? summary, string? content, EntryContentKind contentKind,
        DateTimeOffset? publishedAt = null, DateTimeOffset? updatedAt = null,
        IEnumerable<EntryCategory>? categories = null, IEnumerable<EntryAuthor>? authors = null,
        IEnumerable<EntryEnclosure>? enclosures = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (!Enum.IsDefined(identityKind) || !Enum.IsDefined(contentKind))
        {
            throw new ArgumentException("未定義の識別子種別または本文種別です。");
        }
        if (link is { IsAbsoluteUri: false }
            || (identityKind == EntryIdentityKind.Link && link is null)
            || (identityKind == EntryIdentityKind.NativeId && string.IsNullOrWhiteSpace(nativeId)))
        {
            throw new ArgumentException("記事には元 ID または絶対リンクが必要です。");
        }
        var enclosureArray = enclosures?.ToArray() ?? [];
        if (enclosureArray.Any(item => !item.Url.IsAbsoluteUri || item.Length < 0))
        {
            throw new ArgumentException("enclosure の URL またはサイズが不正です。");
        }
        SourceId = sourceId;
        NativeId = nativeId;
        IdentityKind = identityKind;
        Link = link;
        Title = title;
        Summary = summary;
        Content = content;
        ContentKind = contentKind;
        PublishedAt = publishedAt?.ToUniversalTime();
        UpdatedAt = updatedAt?.ToUniversalTime();
        Categories = Array.AsReadOnly(categories?.ToArray() ?? []);
        Authors = Array.AsReadOnly(authors?.ToArray() ?? []);
        Enclosures = Array.AsReadOnly(enclosureArray);
    }

    public string SourceId { get; }
    public string? NativeId { get; }
    public EntryIdentityKind IdentityKind { get; }
    public Uri? Link { get; }
    public string? Title { get; }
    public string? Summary { get; }
    public string? Content { get; }
    public EntryContentKind ContentKind { get; }
    public DateTimeOffset? PublishedAt { get; }
    public DateTimeOffset? UpdatedAt { get; }
    public IReadOnlyList<EntryCategory> Categories { get; }
    public IReadOnlyList<EntryAuthor> Authors { get; }
    public IReadOnlyList<EntryEnclosure> Enclosures { get; }
}
