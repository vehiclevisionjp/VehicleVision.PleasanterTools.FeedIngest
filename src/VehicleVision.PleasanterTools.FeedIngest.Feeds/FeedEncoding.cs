using System.Text;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>
/// フィードのバイト列を文字列へ復号する（RFC 7303 §3 に沿う）。
/// </summary>
/// <remarks>
/// BOM があればそれを優先し、無ければ HTTP の charset、それも無ければ XML 宣言、無ければ UTF-8。
/// 選んだ文字コードと XML 宣言・明示 charset が食い違う場合、未知の文字コード、不正バイトは
/// 置換文字で続けず <see cref="FeedParseException"/>（Encoding）にする。UTF-8／UTF-16 のみ対応。
/// </remarks>
internal static partial class FeedEncoding
{
    private enum Family
    {
        Utf8,
        Utf16Le,
        Utf16Be,
        Utf16Unmarked,
    }

    public static string Decode(ReadOnlySpan<byte> bytes, string? httpCharset)
    {
        var (family, bomLength) = DetectBom(bytes);
        var chosenByBom = bomLength > 0;
        var content = bytes[bomLength..];

        Family? explicitCharset = string.IsNullOrWhiteSpace(httpCharset) ? null : ParseName(httpCharset, "charset");
        Family? declared = null;
        var declaration = ReadDeclaration(content, family);
        if (declaration is not null)
        {
            declared = ParseName(declaration, "XML 宣言");
        }

        Family chosen;
        if (chosenByBom)
        {
            chosen = family;
        }
        else if (explicitCharset is not null)
        {
            chosen = explicitCharset.Value;
        }
        else if (declared is not null)
        {
            chosen = declared.Value;
        }
        else
        {
            chosen = SniffWithoutBom(content);
        }

        if (chosen == Family.Utf16Unmarked)
        {
            chosen = SniffWithoutBom(content) == Family.Utf16Be ? Family.Utf16Be : Family.Utf16Le;
        }

        // 明示 charset と宣言の食い違い、選んだ文字コードと宣言の食い違いは止める
        if (declared is not null && !Compatible(chosen, declared.Value))
        {
            throw new FeedParseException(FeedParseErrorKind.Encoding, "文字コードの指定が矛盾しています。");
        }

        if (!chosenByBom && explicitCharset is not null && !Compatible(chosen, explicitCharset.Value))
        {
            throw new FeedParseException(FeedParseErrorKind.Encoding, "文字コードの指定が矛盾しています。");
        }

        Encoding encoding = chosen switch
        {
            Family.Utf8 => new UTF8Encoding(false, true),
            Family.Utf16Be => new UnicodeEncoding(true, false, true),
            _ => new UnicodeEncoding(false, false, true),
        };

        try
        {
            return encoding.GetString(content);
        }
        catch (ArgumentException)
        {
            throw new FeedParseException(FeedParseErrorKind.Encoding, "不正なバイト列を含みます。");
        }
    }

    private static bool Compatible(Family chosen, Family other)
        => chosen == other
            || other == Family.Utf16Unmarked && chosen is Family.Utf16Le or Family.Utf16Be;

    private static (Family Family, int BomLength) DetectBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Family.Utf8, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Family.Utf16Le, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Family.Utf16Be, 2);
        }

        return (Family.Utf8, 0);
    }

    /// <summary>BOM も指定も無いときの判定。XML の先頭 <c>&lt;?</c> の並びで UTF-16 を見分ける。</summary>
    private static Family SniffWithoutBom(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 4 && content[0] == 0x3C && content[1] == 0x00 && content[2] != 0x00)
        {
            return Family.Utf16Le;
        }

        if (content.Length >= 4 && content[0] == 0x00 && content[1] == 0x3C)
        {
            return Family.Utf16Be;
        }

        return Family.Utf8;
    }

    private static Family ParseName(string name, string origin)
    {
        switch (name.Trim().Trim('"', '\'').ToLowerInvariant())
        {
            case "utf-8":
            case "utf8":
                return Family.Utf8;
            case "utf-16le":
                return Family.Utf16Le;
            case "utf-16be":
                return Family.Utf16Be;
            case "utf-16":
            case "utf16":
                return Family.Utf16Unmarked;
            default:
                throw new FeedParseException(
                    FeedParseErrorKind.Encoding, $"{origin} の文字コードは未対応です（UTF-8／UTF-16 のみ）。");
        }
    }

    /// <summary>XML 宣言の encoding を、ASCII 互換な先頭から読む。UTF-16 は 1 バイト置きに読む。</summary>
    private static string? ReadDeclaration(ReadOnlySpan<byte> content, Family family)
    {
        var head = content[..Math.Min(content.Length, 256)];
        var builder = new StringBuilder(head.Length);
        var wide = family is Family.Utf16Le or Family.Utf16Be || SniffWithoutBom(content) != Family.Utf8;
        var step = wide ? 2 : 1;
        var offset = wide && (family == Family.Utf16Be || SniffWithoutBom(content) == Family.Utf16Be) ? 1 : 0;
        for (var i = offset; i < head.Length; i += step)
        {
            builder.Append((char)head[i]);
        }

        var match = DeclarationPattern().Match(builder.ToString());
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""^\s*<\?xml[^>]*?\sencoding\s*=\s*["']([^"']+)["']""", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationPattern();
}
