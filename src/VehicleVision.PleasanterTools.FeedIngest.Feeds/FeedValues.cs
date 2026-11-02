using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>1 項目を正規化できないときに投げる。他の項目の処理は続く。</summary>
internal sealed class FeedItemException(string reason) : Exception(reason);

/// <summary>日時・URI・テキストの読み取りの共通処理。</summary>
internal static partial class FeedValues
{
    private static readonly string[] Months =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly Dictionary<string, int> NamedZones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GMT"] = 0, ["UT"] = 0, ["UTC"] = 0, ["Z"] = 0,
        ["EST"] = -5, ["EDT"] = -4, ["CST"] = -6, ["CDT"] = -5,
        ["MST"] = -7, ["MDT"] = -6, ["PST"] = -8, ["PDT"] = -7,
    };

    /// <summary>RFC 822（RSS の pubDate）。オフセットの無い日時・不正な日時は項目エラー。</summary>
    public static DateTimeOffset ParseRfc822(string value)
    {
        var match = Rfc822Pattern().Match(value.Trim());
        if (!match.Success)
        {
            throw new FeedItemException("日時の形式が不正です。");
        }

        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        var month = Array.IndexOf(Months, match.Groups["mon"].Value.ToLowerInvariant()) + 1;
        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        if (match.Groups["y"].Value.Length == 2)
        {
            year += year < 50 ? 2000 : 1900;
        }

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var second = match.Groups["s"].Success
            ? int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture)
            : 0;

        var zone = match.Groups["z"].Value;
        TimeSpan offset;
        if (NamedZones.TryGetValue(zone, out var hours))
        {
            offset = TimeSpan.FromHours(hours);
        }
        else
        {
            var sign = zone[0] == '-' ? -1 : 1;
            var zoneHours = int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture);
            var zoneMinutes = int.Parse(zone.AsSpan(3, 2), CultureInfo.InvariantCulture);
            if (zoneHours > 14 || zoneMinutes > 59)
            {
                throw new FeedItemException("日時のオフセットが不正です。");
            }

            offset = sign * new TimeSpan(zoneHours, zoneMinutes, 0);
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, offset).ToUniversalTime();
        }
        catch (ArgumentException)
        {
            throw new FeedItemException("日時の値が範囲外です。");
        }
    }

    /// <summary>RFC 3339（Atom）。オフセット（Z を含む）が無い日時は項目エラー。</summary>
    public static DateTimeOffset ParseRfc3339(string value)
    {
        var text = value.Trim();
        if (!Rfc3339Pattern().IsMatch(text)
            || !DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new FeedItemException("日時の形式が不正です。");
        }

        return parsed.ToUniversalTime();
    }

    /// <summary>
    /// <c>xml:base</c> を祖先から順にたどり、要素の基準 URI を返す。
    /// </summary>
    public static Uri BaseOf(XElement element, Uri documentBase)
    {
        var chain = new Stack<string>();
        for (XElement? current = element; current is not null; current = current.Parent)
        {
            var attribute = current.Attribute(XNamespace.Xml + "base");
            if (attribute is not null)
            {
                chain.Push(attribute.Value);
            }
        }

        var result = documentBase;
        while (chain.Count > 0)
        {
            if (!Uri.TryCreate(result, chain.Pop().Trim(), out var next) || !IsWebUri(next))
            {
                throw new FeedItemException("xml:base が不正です。");
            }

            result = next;
        }

        return result;
    }

    /// <summary>相対 URI を基準で解決し、http／https の絶対 URI にする。</summary>
    public static Uri ResolveWebUri(Uri baseUri, string value, string what)
    {
        if (!Uri.TryCreate(baseUri, value.Trim(), out var resolved) || !IsWebUri(resolved))
        {
            throw new FeedItemException($"{what} が絶対の http／https URI になりません。");
        }

        return resolved;
    }

    public static bool IsWebUri(Uri uri)
        => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>宣言されたサイズ。解釈できない・負の値は不明（null）として扱う。</summary>
    public static long? ParseLength(string? value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : null;

    public static string? ChildText(XElement parent, XName name)
        => NullIfBlank(parent.Element(name)?.Value);

    [GeneratedRegex(
        @"^(?:[A-Za-z]{3},\s*)?(?<d>\d{1,2})\s+(?<mon>[A-Za-z]{3})\s+(?<y>\d{4}|\d{2})\s+(?<h>\d{1,2}):(?<m>\d{2})(?::(?<s>\d{2}))?\s+(?<z>[A-Za-z]{1,3}|[+-]\d{4})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Rfc822Pattern();

    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339Pattern();

}
