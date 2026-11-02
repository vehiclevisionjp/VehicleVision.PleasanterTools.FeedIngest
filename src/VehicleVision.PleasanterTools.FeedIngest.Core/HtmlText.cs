using System.Net;
using System.Text;

namespace VehicleVision.PleasanterTools.FeedIngest.Core;

/// <summary>
/// 未信頼の HTML を、実行も取得もせずプレーンテキストへ変換する。
/// </summary>
/// <remarks>
/// <para>出力はテキストであり、HTML のサニタイズ結果ではない（タグ・属性・スクリプトは出力へ残さない）。
/// 画像・スクリプト・リンク先の取得は行わない。正規表現による除去ではなく、字句を順に読む。</para>
/// <para>script／style の中身は出力しない。ブロック要素は改行、連続する空白は 1 つに畳む。</para>
/// </remarks>
public static class HtmlText
{
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "ul", "ol", "tr", "table", "blockquote", "pre", "hr",
        "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "header", "footer",
        "aside", "nav", "figure", "figcaption", "dl", "dt", "dd", "form", "fieldset", "address",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style",
    };

    /// <summary>HTML をプレーンテキストにする。<c>null</c> は <c>null</c> を返す。</summary>
    public static string? ToPlainText(string? html)
    {
        if (html is null)
        {
            return null;
        }

        var builder = new StringBuilder(html.Length);
        var text = new StringBuilder();
        var position = 0;
        while (position < html.Length)
        {
            var current = html[position];
            if (current != '<' || position + 1 >= html.Length || !IsTagStart(html[position + 1]))
            {
                text.Append(current);
                position++;
                continue;
            }

            FlushText(text, builder);
            position = ReadMarkup(html, position, builder);
        }

        FlushText(text, builder);
        return Normalize(builder.ToString());
    }

    private static bool IsTagStart(char next) => char.IsAsciiLetter(next) || next is '/' or '!' or '?';

    private static void FlushText(StringBuilder text, StringBuilder output)
    {
        if (text.Length == 0)
        {
            return;
        }

        output.Append(WebUtility.HtmlDecode(text.ToString()));
        text.Clear();
    }

    /// <summary>タグ・コメント等を 1 つ読み、次の位置を返す。</summary>
    private static int ReadMarkup(string html, int start, StringBuilder output)
    {
        if (string.CompareOrdinal(html, start, "<!--", 0, 4) == 0)
        {
            var close = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
            return close < 0 ? html.Length : close + 3;
        }

        var position = start + 1;
        var closing = false;
        if (html[position] == '/')
        {
            closing = true;
            position++;
        }

        var nameStart = position;
        while (position < html.Length && char.IsAsciiLetterOrDigit(html[position]))
        {
            position++;
        }

        var name = html[nameStart..position];
        position = SkipToTagEnd(html, position);
        if (name.Length == 0)
        {
            return position;
        }

        if (!closing && RawTextElements.Contains(name))
        {
            var end = html.IndexOf($"</{name}", position, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                return html.Length;
            }

            return SkipToTagEnd(html, end + 2 + name.Length);
        }

        if (closing && name is "li" or "tr" or "dt" or "dd")
        {
            return position;
        }

        if (BlockElements.Contains(name))
        {
            output.Append('\n');
        }
        else if (name.Equals("td", StringComparison.OrdinalIgnoreCase)
            || name.Equals("th", StringComparison.OrdinalIgnoreCase))
        {
            output.Append(' ');
        }

        return position;
    }

    /// <summary>引用符の中の <c>&gt;</c> を飛ばして、タグの終わりの次を返す。</summary>
    private static int SkipToTagEnd(string html, int position)
    {
        char? quote = null;
        while (position < html.Length)
        {
            var current = html[position++];
            if (quote is not null)
            {
                if (current == quote)
                {
                    quote = null;
                }
            }
            else if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == '>')
            {
                return position;
            }
        }

        return html.Length;
    }

    private static string Normalize(string value)
    {
        var lines = new List<string>();
        foreach (var rawLine in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = new StringBuilder(rawLine.Length);
            var pendingSpace = false;
            foreach (var character in rawLine)
            {
                if (char.IsWhiteSpace(character) || character == ' ')
                {
                    pendingSpace = line.Length > 0;
                    continue;
                }

                if (char.IsControl(character))
                {
                    continue;
                }

                if (pendingSpace)
                {
                    line.Append(' ');
                    pendingSpace = false;
                }

                line.Append(character);
            }

            lines.Add(line.ToString());
        }

        var result = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (result.Length > 0)
            {
                result.Append('\n');
                if (blank > 0)
                {
                    result.Append('\n');
                }
            }

            blank = 0;
            result.Append(line);
        }

        return result.ToString();
    }
}
