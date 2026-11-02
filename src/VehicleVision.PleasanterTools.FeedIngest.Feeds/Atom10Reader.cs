using System.Text;
using System.Xml.Linq;
using VehicleVision.PleasanterTools.FeedIngest.Core;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>Atom 1.0 の entry を <see cref="NormalizedEntry"/> へ変換する。</summary>
internal static class Atom10Reader
{
    public static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    public static IEnumerable<XElement> Items(XElement root) => root.Elements(Atom + "entry");

    public static NormalizedEntry Read(
        XElement entry, string sourceId, Uri documentBase, FeedParserLimits limits)
    {
        var baseUri = FeedValues.BaseOf(entry, documentBase);

        var id = FeedValues.ChildText(entry, Atom + "id")
            ?? throw new FeedItemException("id がありません。");

        var link = SelectLink(entry, baseUri);

        var (content, contentKind) = ReadContent(entry);
        if (content is not null && Encoding.UTF8.GetByteCount(content) > limits.MaxItemContentBytes)
        {
            throw new FeedItemException("記事本文が上限を超えています。");
        }

        var summaryText = PlainText(entry.Element(Atom + "summary"));
        var titleText = PlainText(entry.Element(Atom + "title"));

        var publishedText = FeedValues.ChildText(entry, Atom + "published");
        var updatedText = FeedValues.ChildText(entry, Atom + "updated");

        return new NormalizedEntry(
            sourceId,
            id,
            EntryIdentityKind.NativeId,
            link,
            titleText,
            summaryText,
            content,
            contentKind,
            publishedText is null ? null : FeedValues.ParseRfc3339(publishedText),
            updatedText is null ? null : FeedValues.ParseRfc3339(updatedText),
            ReadCategories(entry),
            ReadAuthors(entry),
            ReadEnclosures(entry, baseUri));
    }

    /// <summary>rel が無い・alternate のうち、HTML を優先して 1 つ選ぶ。</summary>
    private static Uri? SelectLink(XElement entry, Uri baseUri)
    {
        XElement? chosen = null;
        foreach (var link in entry.Elements(Atom + "link"))
        {
            var rel = (string?)link.Attribute("rel");
            if (rel is not (null or "" or "alternate") || (string?)link.Attribute("href") is null)
            {
                continue;
            }

            var type = (string?)link.Attribute("type");
            if (type is "text/html" or "application/xhtml+xml")
            {
                chosen = link;
                break;
            }

            chosen ??= link;
        }

        if (chosen is null)
        {
            return null;
        }

        return FeedValues.ResolveWebUri(
            FeedValues.BaseOf(chosen, baseUri), (string)chosen.Attribute("href")!, "link");
    }

    /// <summary>タイトル・要約。xhtml は子要素のテキストを連結し、html はそのまま返す。</summary>
    private static (string? Text, string Type) ReadText(XElement? element)
    {
        if (element is null)
        {
            return (null, "text");
        }

        var type = ((string?)element.Attribute("type") ?? "text").Trim();
        if (type == "xhtml")
        {
            var div = element.Element(Xhtml + "div");
            return (FeedValues.NullIfBlank(div?.Value), type);
        }

        return (FeedValues.NullIfBlank(element.Value), type);
    }

    /// <summary>タイトル・要約をプレーンテキストにする。html だけ変換し、text／xhtml はそのまま。</summary>
    private static string? PlainText(XElement? element)
    {
        var (text, type) = ReadText(element);
        return type == "html" && text is not null ? HtmlText.ToPlainText(text) : text;
    }

    private static (string? Content, EntryContentKind Kind) ReadContent(XElement entry)
    {
        var element = entry.Element(Atom + "content");
        if (element is null)
        {
            var (summary, summaryType) = ReadText(entry.Element(Atom + "summary"));
            return (summary, summaryType == "html" ? EntryContentKind.Html : EntryContentKind.Text);
        }

        if (element.Attribute("src") is not null)
        {
            // 外部の本文は取得しない
            return (null, EntryContentKind.External);
        }

        var type = ((string?)element.Attribute("type") ?? "text").Trim();
        switch (type)
        {
            case "xhtml":
                var div = element.Element(Xhtml + "div");
                var inner = div is null ? null : string.Concat(div.Nodes());
                return (FeedValues.NullIfBlank(inner), EntryContentKind.Xhtml);
            case "text":
            case "html":
                return (FeedValues.NullIfBlank(element.Value), KindOf(type));
            default:
                // text/* はテキスト。それ以外（base64 のバイナリ等）は展開しない
                return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                    ? (FeedValues.NullIfBlank(element.Value), EntryContentKind.Text)
                    : (null, EntryContentKind.Binary);
        }
    }

    private static EntryContentKind KindOf(string type) => type switch
    {
        "html" => EntryContentKind.Html,
        "xhtml" => EntryContentKind.Xhtml,
        _ => EntryContentKind.Text,
    };

    private static List<EntryCategory> ReadCategories(XElement entry)
    {
        var result = new List<EntryCategory>();
        foreach (var category in entry.Elements(Atom + "category"))
        {
            var term = FeedValues.NullIfBlank((string?)category.Attribute("term"));
            if (term is not null)
            {
                result.Add(new EntryCategory(
                    term,
                    FeedValues.NullIfBlank((string?)category.Attribute("scheme")),
                    FeedValues.NullIfBlank((string?)category.Attribute("label"))));
            }
        }

        return result;
    }

    /// <summary>entry の author。無ければ feed の author を使う（RFC 4287 §4.2.1）。</summary>
    private static List<EntryAuthor> ReadAuthors(XElement entry)
    {
        var authors = entry.Elements(Atom + "author").ToList();
        if (authors.Count == 0 && entry.Parent is { } feed)
        {
            authors = feed.Elements(Atom + "author").ToList();
        }

        var result = new List<EntryAuthor>();
        foreach (var author in authors)
        {
            var name = FeedValues.ChildText(author, Atom + "name");
            if (name is null)
            {
                continue;
            }

            var uriText = FeedValues.ChildText(author, Atom + "uri");
            Uri.TryCreate(uriText, UriKind.Absolute, out var uri);
            result.Add(new EntryAuthor(name, FeedValues.ChildText(author, Atom + "email"), uri));
        }

        return result;
    }

    private static List<EntryEnclosure> ReadEnclosures(XElement entry, Uri baseUri)
    {
        var result = new List<EntryEnclosure>();
        foreach (var link in entry.Elements(Atom + "link"))
        {
            if ((string?)link.Attribute("rel") != "enclosure")
            {
                continue;
            }

            var href = FeedValues.NullIfBlank((string?)link.Attribute("href"))
                ?? throw new FeedItemException("enclosure の href がありません。");
            result.Add(new EntryEnclosure(
                FeedValues.ResolveWebUri(FeedValues.BaseOf(link, baseUri), href, "enclosure の href"),
                FeedValues.NullIfBlank((string?)link.Attribute("type")),
                FeedValues.ParseLength((string?)link.Attribute("length"))));
        }

        return result;
    }
}
