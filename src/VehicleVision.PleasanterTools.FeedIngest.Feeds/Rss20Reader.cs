using System.Text;
using System.Xml.Linq;
using VehicleVision.PleasanterTools.FeedIngest.Core;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>RSS 2.0 の項目を <see cref="NormalizedEntry"/> へ変換する。</summary>
internal static class Rss20Reader
{
    /// <summary>content:encoded の名前空間（RSS 2.0 本体ではない拡張。この名前空間だけを対応する）。</summary>
    public static readonly XNamespace ContentNamespace = "http://purl.org/rss/1.0/modules/content/";

    private static readonly XNamespace DublinCore = "http://purl.org/dc/elements/1.1/";

    public static IEnumerable<XElement> Items(XElement root)
        => root.Elements("channel").SelectMany(channel => channel.Elements("item"));

    public static NormalizedEntry Read(
        XElement item, string sourceId, Uri documentBase, FeedParserLimits limits)
    {
        var baseUri = FeedValues.BaseOf(item, documentBase);

        var guid = FeedValues.ChildText(item, "guid");
        var linkText = FeedValues.ChildText(item, "link");
        Uri? link = linkText is null ? null : FeedValues.ResolveWebUri(baseUri, linkText, "link");

        EntryIdentityKind kind;
        if (guid is not null)
        {
            kind = EntryIdentityKind.NativeId;
        }
        else if (link is not null)
        {
            kind = EntryIdentityKind.Link;
        }
        else
        {
            throw new FeedItemException("guid も link もありません。");
        }

        var description = FeedValues.NullIfBlank(item.Element("description")?.Value);
        var encoded = FeedValues.NullIfBlank(item.Element(ContentNamespace + "encoded")?.Value);
        var content = encoded ?? description;
        if (content is not null && Encoding.UTF8.GetByteCount(content) > limits.MaxItemContentBytes)
        {
            throw new FeedItemException("記事本文が上限を超えています。");
        }

        var publishedText = FeedValues.ChildText(item, "pubDate");
        DateTimeOffset? published = publishedText is null ? null : FeedValues.ParseRfc822(publishedText);

        var title = FeedValues.ChildText(item, "title");
        return new NormalizedEntry(
            sourceId,
            guid,
            kind,
            link,
            title is null ? null : HtmlText.ToPlainText(title),
            description is null ? null : HtmlText.ToPlainText(description),
            content,
            content is null ? EntryContentKind.Text : EntryContentKind.Html,
            published,
            null,
            ReadCategories(item),
            ReadAuthors(item),
            ReadEnclosures(item, baseUri));
    }

    private static List<EntryCategory> ReadCategories(XElement item)
    {
        var result = new List<EntryCategory>();
        foreach (var category in item.Elements("category"))
        {
            var term = FeedValues.NullIfBlank(category.Value);
            if (term is not null)
            {
                result.Add(new EntryCategory(term, FeedValues.NullIfBlank((string?)category.Attribute("domain"))));
            }
        }

        return result;
    }

    private static List<EntryAuthor> ReadAuthors(XElement item)
    {
        var result = new List<EntryAuthor>();
        foreach (var author in item.Elements("author"))
        {
            var parsed = ParseAuthor(author.Value);
            if (parsed is not null)
            {
                result.Add(parsed);
            }
        }

        foreach (var creator in item.Elements(DublinCore + "creator"))
        {
            var name = FeedValues.NullIfBlank(creator.Value);
            if (name is not null)
            {
                result.Add(new EntryAuthor(name));
            }
        }

        return result;
    }

    /// <summary>RSS の author は <c>mail (名前)</c> が規定。<c>名前 &lt;mail&gt;</c> と名前だけも受ける。</summary>
    internal static EntryAuthor? ParseAuthor(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (open > 0 && close > open)
        {
            var email = text[..open].Trim();
            var name = text[(open + 1)..close].Trim();
            if (email.Contains('@', StringComparison.Ordinal) && name.Length > 0)
            {
                return new EntryAuthor(name, email);
            }
        }

        var lt = text.IndexOf('<', StringComparison.Ordinal);
        var gt = text.LastIndexOf('>');
        if (lt > 0 && gt > lt)
        {
            var name = text[..lt].Trim();
            var email = text[(lt + 1)..gt].Trim();
            if (name.Length > 0 && email.Contains('@', StringComparison.Ordinal))
            {
                return new EntryAuthor(name, email);
            }
        }

        return text.Contains('@', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal)
            ? new EntryAuthor(text, text)
            : new EntryAuthor(text);
    }

    private static List<EntryEnclosure> ReadEnclosures(XElement item, Uri baseUri)
    {
        var result = new List<EntryEnclosure>();
        foreach (var enclosure in item.Elements("enclosure"))
        {
            var url = FeedValues.NullIfBlank((string?)enclosure.Attribute("url"))
                ?? throw new FeedItemException("enclosure の url がありません。");
            result.Add(new EntryEnclosure(
                FeedValues.ResolveWebUri(FeedValues.BaseOf(enclosure, baseUri), url, "enclosure の url"),
                FeedValues.NullIfBlank((string?)enclosure.Attribute("type")),
                FeedValues.ParseLength((string?)enclosure.Attribute("length"))));
        }

        return result;
    }
}
