using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;

/// <summary>
/// 記事の外部キー（<c>fi:v1:</c> ＋ SHA-256 の小文字 hex 64 桁）。重複排除と Pleasanter 側の照合に使う。
/// </summary>
/// <remarks>
/// ハッシュの入力は <c>["fi:v1", sourceId, identityKind, identity]</c> の決定的な JSON 配列（UTF-8）。
/// 文字列の単純連結は境界が衝突するため使わない。JSON の文字列は <c>"</c>・<c>\</c>・制御文字
/// （<c>\b \f \n \r \t</c>、他は <c>\u00xx</c>）だけを escape し、それ以外は UTF-8 のまま書く。
/// identityKind は <see cref="EntryIdentityKind"/> の名前（<c>NativeId</c> / <c>Link</c>）。
/// 元 ID（guid／id）の大文字小文字・内部文字は変えない。購読元ごとの重複排除であり、
/// siteId は含めない（配送先は別に区切る）。
/// </remarks>
public static class ExternalKey
{
    public const string Prefix = "fi:v1:";

    /// <summary>記事の外部キーを計算する。</summary>
    public static string For(NormalizedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Compute(entry.SourceId, entry.IdentityKind, IdentityOf(entry));
    }

    public static string Compute(string sourceId, EntryIdentityKind kind, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(identity);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException("未定義の識別子種別です。", nameof(kind));
        }

        var json = new StringBuilder("[");
        AppendString(json, "fi:v1");
        json.Append(',');
        AppendString(json, sourceId);
        json.Append(',');
        AppendString(json, kind.ToString());
        json.Append(',');
        AppendString(json, identity);
        json.Append(']');
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString())));
    }

    /// <summary>
    /// 識別に使う文字列。元 ID はそのまま。link は <see cref="Uri"/> の正規形（scheme・host の小文字化、既定ポートの省略、
    /// 百分率エンコードの正規化、dot segment の解決を含む）にする。fragment は含めない。path・query の大小と並びは保つ。
    /// </summary>
    public static string IdentityOf(NormalizedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IdentityKind == EntryIdentityKind.NativeId)
        {
            return entry.NativeId!;
        }

        var link = entry.Link!;
        return link.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
