using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using VehicleVision.PleasanterTools.FeedIngest.Core;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>
/// 取得済みのバイト列（RSS 2.0 / Atom 1.0）を <see cref="NormalizedEntry"/> へ変換する。通信しない。
/// </summary>
/// <remarks>
/// <para>XML は DTD 禁止・外部リソース解決なしで読む。本文の HTML は実行も取得もしない。</para>
/// <para>フィード全体の不正（文字コード・XML・形式・上限）は <see cref="FeedParseException"/>、
/// 1 項目の不正は <see cref="FeedParseResult.ItemErrors"/> に集めて他の項目を続ける。</para>
/// </remarks>
public static class FeedParser
{
    /// <param name="content">取得した本文（圧縮は解除済み）。</param>
    /// <param name="sourceId">取り込み元の安定 ID。</param>
    /// <param name="feedUrl">最終取得 URL（相対 URI の解決の基準。絶対 http／https）。</param>
    /// <param name="expectedFormat">設定の形式。<see cref="FeedFormat.Auto"/> は 2 形式から判定する。</param>
    /// <param name="httpCharset">Content-Type の charset。無ければ <c>null</c>。</param>
    /// <param name="limits">上限。</param>
    public static FeedParseResult Parse(
        ReadOnlySpan<byte> content,
        string sourceId,
        Uri feedUrl,
        FeedFormat expectedFormat = FeedFormat.Auto,
        string? httpCharset = null,
        FeedParserLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(feedUrl);
        if (!FeedValues.IsWebUri(feedUrl))
        {
            throw new ArgumentException("フィード URL は絶対の http／https URI にします。", nameof(feedUrl));
        }

        limits ??= new FeedParserLimits();
        if (content.Length > limits.MaxBytes)
        {
            throw new FeedParseException(FeedParseErrorKind.LimitExceeded, "フィードが容量の上限を超えています。");
        }

        var text = FeedEncoding.Decode(content, httpCharset);
        var root = LoadRoot(text, limits);
        var format = DetectFormat(root, expectedFormat);

        var items = (format == FeedFormat.Rss20 ? Rss20Reader.Items(root) : Atom10Reader.Items(root)).ToList();
        if (items.Count > limits.MaxItems)
        {
            throw new FeedParseException(FeedParseErrorKind.LimitExceeded, "記事数が上限を超えています。");
        }

        var documentBase = FeedValues.BaseOf(root, feedUrl);
        var entries = new List<(int Index, NormalizedEntry Entry)>();
        var errors = new List<FeedItemError>();
        for (var index = 0; index < items.Count; index++)
        {
            try
            {
                var entry = format == FeedFormat.Rss20
                    ? Rss20Reader.Read(items[index], sourceId, documentBase, limits)
                    : Atom10Reader.Read(items[index], sourceId, documentBase, limits);
                entries.Add((index, entry));
            }
            catch (FeedItemException ex)
            {
                errors.Add(new FeedItemError(index, ex.Message));
            }
            catch (ArgumentException)
            {
                // NormalizedEntry の不変条件に反する項目
                errors.Add(new FeedItemError(index, "正規化モデルの条件を満たしません。"));
            }
        }

        var (unique, conflicts) = Deduplicate(entries);
        return new FeedParseResult(format, unique, errors, conflicts);
    }

    private static XElement LoadRoot(string text, FeedParserLimits limits)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaxBytes,
            MaxCharactersFromEntities = 0,
            CloseInput = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var stringReader = new StringReader(text);
            using var reader = XmlReader.Create(stringReader, settings);
            return XDocument.Load(reader, LoadOptions.None).Root
                ?? throw new FeedParseException(FeedParseErrorKind.Xml, "XML のルート要素がありません。");
        }
        catch (XmlException)
        {
            // 位置・本文を載せない
            throw new FeedParseException(FeedParseErrorKind.Xml, "XML として不正です（DTD を含む場合も拒否します）。");
        }
    }

    private static FeedFormat DetectFormat(XElement root, FeedFormat expected)
    {
        FeedFormat detected;
        if (root.Name == "rss")
        {
            if ((string?)root.Attribute("version") != "2.0")
            {
                throw new FeedParseException(FeedParseErrorKind.UnsupportedFormat, "RSS 2.0 以外のバージョンは未対応です。");
            }

            detected = FeedFormat.Rss20;
        }
        else if (root.Name == Atom10Reader.Atom + "feed")
        {
            detected = FeedFormat.Atom10;
        }
        else
        {
            throw new FeedParseException(
                FeedParseErrorKind.UnsupportedFormat, "RSS 2.0 / Atom 1.0 以外の形式です（RDF・JSON Feed 等は未対応）。");
        }

        if (expected != FeedFormat.Auto && expected != detected)
        {
            throw new FeedParseException(FeedParseErrorKind.UnsupportedFormat, "設定した形式とフィードの形式が異なります。");
        }

        return detected;
    }

    /// <summary>同じ ID・同じ内容は 1 件にまとめ、同じ ID・異なる内容は競合として除く。</summary>
    private static (List<NormalizedEntry> Entries, List<FeedItemConflict> Conflicts) Deduplicate(
        List<(int Index, NormalizedEntry Entry)> entries)
    {
        var groups = new Dictionary<(EntryIdentityKind, string), List<NormalizedEntry>>();
        var order = new List<(EntryIdentityKind, string)>();
        foreach (var (_, entry) in entries)
        {
            var key = (entry.IdentityKind, Identity(entry));
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
                order.Add(key);
            }

            list.Add(entry);
        }

        var unique = new List<NormalizedEntry>();
        var conflicts = new List<FeedItemConflict>();
        foreach (var key in order)
        {
            var variants = groups[key].DistinctBy(Signature, StringComparer.Ordinal).ToList();
            if (variants.Count == 1)
            {
                unique.Add(variants[0]);
            }
            else
            {
                conflicts.Add(new FeedItemConflict(key.Item1, key.Item2, variants.Count));
            }
        }

        return (unique, conflicts);
    }

    private static string Identity(NormalizedEntry entry)
        => entry.IdentityKind == EntryIdentityKind.NativeId ? entry.NativeId! : entry.Link!.AbsoluteUri;

    /// <summary>内容の同一性を、全フィールドの決定的な JSON で見る。</summary>
    private static string Signature(NormalizedEntry entry) => JsonSerializer.Serialize(new object?[]
    {
        entry.NativeId, entry.IdentityKind, entry.Link?.AbsoluteUri, entry.Title, entry.Summary,
        entry.Content, entry.ContentKind, entry.PublishedAt, entry.UpdatedAt,
        entry.Categories, entry.Authors, entry.Enclosures,
    });
}
