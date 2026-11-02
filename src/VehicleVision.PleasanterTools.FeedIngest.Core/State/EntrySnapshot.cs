using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.State;

/// <summary>
/// 正規化記事の保存用 JSON（スナップショット）と、その内容ハッシュ。
/// </summary>
/// <remarks>
/// 同じ記事は常に同じ JSON になる（プロパティの順序・日時の書式を固定し、現在時刻・乱数を含めない）。
/// 本文・enclosure の URL は秘密相当として扱う（DB のアクセス制限は運用側）。
/// </remarks>
public static class EntrySnapshot
{
    private const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static string ToJson(NormalizedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return JsonSerializer.Serialize(Dto.From(entry), Options);
    }

    public static NormalizedEntry FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        var dto = JsonSerializer.Deserialize<Dto>(json, Options)
            ?? throw new InvalidOperationException("記事のスナップショットを読めません。");
        if (dto.V != Version)
        {
            throw new InvalidOperationException("未知の版の記事スナップショットです。");
        }

        return dto.ToEntry();
    }

    /// <summary>スナップショットの JSON の SHA-256（小文字 hex、64 桁）。</summary>
    public static string HashOf(string json)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private sealed class Dto
    {
        public int V { get; set; }

        public string SourceId { get; set; } = string.Empty;

        public string? NativeId { get; set; }

        public string IdentityKind { get; set; } = string.Empty;

        public string? Link { get; set; }

        public string? Title { get; set; }

        public string? Summary { get; set; }

        public string? Content { get; set; }

        public string ContentKind { get; set; } = string.Empty;

        public string? PublishedAt { get; set; }

        public string? UpdatedAt { get; set; }

        public List<CategoryDto> Categories { get; set; } = [];

        public List<AuthorDto> Authors { get; set; } = [];

        public List<EnclosureDto> Enclosures { get; set; } = [];

        public static Dto From(NormalizedEntry entry) => new()
        {
            V = Version,
            SourceId = entry.SourceId,
            NativeId = entry.NativeId,
            IdentityKind = entry.IdentityKind.ToString(),
            Link = entry.Link?.AbsoluteUri,
            Title = entry.Title,
            Summary = entry.Summary,
            Content = entry.Content,
            ContentKind = entry.ContentKind.ToString(),
            PublishedAt = Format(entry.PublishedAt),
            UpdatedAt = Format(entry.UpdatedAt),
            Categories = entry.Categories.Select(c => new CategoryDto { Term = c.Term, Scheme = c.Scheme, Label = c.Label }).ToList(),
            Authors = entry.Authors.Select(a => new AuthorDto { Name = a.Name, Email = a.Email, Uri = a.Uri?.AbsoluteUri }).ToList(),
            Enclosures = entry.Enclosures.Select(e => new EnclosureDto { Url = e.Url.AbsoluteUri, Type = e.Type, Length = e.Length }).ToList(),
        };

        public NormalizedEntry ToEntry() => new(
            SourceId,
            NativeId,
            Enum.Parse<EntryIdentityKind>(IdentityKind),
            Link is null ? null : new Uri(Link, UriKind.Absolute),
            Title,
            Summary,
            Content,
            Enum.Parse<EntryContentKind>(ContentKind),
            Parse(PublishedAt),
            Parse(UpdatedAt),
            Categories.Select(c => new EntryCategory(c.Term, c.Scheme, c.Label)),
            Authors.Select(a => new EntryAuthor(a.Name, a.Email, a.Uri is null ? null : new Uri(a.Uri, UriKind.Absolute))),
            Enclosures.Select(e => new EntryEnclosure(new Uri(e.Url, UriKind.Absolute), e.Type, e.Length)));

        private static string? Format(DateTimeOffset? value)
            => value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture);

        private static DateTimeOffset? Parse(string? value)
            => value is null
                ? null
                : DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
    }

    private sealed class CategoryDto
    {
        public string Term { get; set; } = string.Empty;

        public string? Scheme { get; set; }

        public string? Label { get; set; }
    }

    private sealed class AuthorDto
    {
        public string Name { get; set; } = string.Empty;

        public string? Email { get; set; }

        public string? Uri { get; set; }
    }

    private sealed class EnclosureDto
    {
        public string Url { get; set; } = string.Empty;

        public string? Type { get; set; }

        public long? Length { get; set; }
    }
}
