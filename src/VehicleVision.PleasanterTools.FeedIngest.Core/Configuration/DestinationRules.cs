using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

/// <summary>
/// 内部ホストの明示許可（U11）の、設定値の形式検査と「許可しても常に拒否する範囲」。
/// </summary>
/// <remarks>
/// 許可リスト（<c>http.allowedNetworks</c>・<c>http.allowedHosts</c>）は source ごとの設定で、
/// ここでは形式と範囲だけを検査する（通信しない）。評価（解決後の IP との照合）は Feeds の <c>DestinationPolicy</c>。
/// </remarks>
public static class DestinationRules
{
    /// <summary>許可の指定として受け付ける IPv4 の最短プレフィックス長（これより広い指定は全許可に近いので拒否する）。</summary>
    public const int MinIPv4Prefix = 8;

    /// <summary>許可の指定として受け付ける IPv6 の最短プレフィックス長（fc00::/7 を許す最小）。</summary>
    public const int MinIPv6Prefix = 7;

    /// <summary>
    /// <c>allowedNetworks</c> に書かれていても拒否し続ける範囲。クラウドのメタデータ・link-local・
    /// 0.0.0.0/8・マルチキャストと予約・IPv4 を埋め込む IPv6 の変換形式。解除する設定は設けない。
    /// </summary>
    private static readonly IPNetwork[] NeverAllowed =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("100.100.100.200/32"),   // Alibaba Cloud のメタデータ
        IPNetwork.Parse("169.254.0.0/16"),       // link-local（AWS・Azure・GCP・OCI のメタデータ 169.254.169.254 を含む）
        IPNetwork.Parse("224.0.0.0/3"),          // マルチキャスト・予約・ブロードキャスト
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("fe80::/10"),            // link-local
        IPNetwork.Parse("fd00:ec2::/32"),        // AWS のメタデータ（IPv6）
        IPNetwork.Parse("ff00::/8"),             // マルチキャスト
        IPNetwork.Parse("2001::/32"),            // Teredo（IPv4 を埋め込む）
        IPNetwork.Parse("2002::/16"),            // 6to4（IPv4 を埋め込む）
        IPNetwork.Parse("64:ff9b::/96"),         // NAT64（IPv4 を埋め込む）
        IPNetwork.Parse("64:ff9b:1::/48"),
    ];

    /// <summary>許可の設定に関係なく拒否するアドレスか（IPv4 射影の IPv6 は IPv4 として評価する）。</summary>
    public static bool IsNeverAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = Normalize(address);
        return NeverAllowed.Any(range => range.Contains(address));
    }

    private static bool HasScope(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0;

    /// <summary>IPv4 射影の IPv6（::ffff:a.b.c.d）を IPv4 へ直す。</summary>
    public static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>CIDR（<c>10.0.0.0/8</c>・<c>fd00::/8</c>）を解釈する。ホスト部が 0 でない・広すぎる・常に拒否の範囲だけは誤り。</summary>
    public static bool TryParseNetwork(string text, out IPNetwork network, out string reason)
    {
        network = default;
        reason = string.Empty;
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == text.Length - 1 || text.Any(char.IsWhiteSpace)
            || !text[(slash + 1)..].All(char.IsAsciiDigit)
            || !IPAddress.TryParse(text.AsSpan(0, slash), out var address)
            || HasScope(address)
            || !int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
            || !IPNetwork.TryParse(text, out network)
            || !network.BaseAddress.Equals(address))    // ホスト部が 0 でない
        {
            network = default;
            reason = "CIDR（例: 10.1.0.0/16）として解釈できないか、ホスト部が 0 ではありません。";
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            network = default;
            reason = "IPv4 射影アドレスは使えません。IPv4 の CIDR で書いてください。";
            return false;
        }

        var v4 = address.AddressFamily == AddressFamily.InterNetwork;
        if (prefix < (v4 ? MinIPv4Prefix : MinIPv6Prefix))
        {
            network = default;
            reason = $"範囲が広すぎます（{(v4 ? "IPv4" : "IPv6")} は /{(v4 ? MinIPv4Prefix : MinIPv6Prefix)} 以上）。全許可にはできません。";
            return false;
        }

        var parsed = network;
        if (NeverAllowed.Any(range => range.BaseAddress.AddressFamily == address.AddressFamily
                && range.PrefixLength <= prefix && range.Contains(parsed.BaseAddress)))
        {
            network = default;
            reason = "常に拒否される範囲（link-local・メタデータ・予約など）で、許可できません。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// ホスト名（または IP リテラル）を、比較用の形（小文字の ASCII・IP は正規形）へ直す。
    /// スキーム・ポート・パス・ワイルドカード・末尾のドットは誤り。
    /// </summary>
    public static bool TryNormalizeHost(string text, out string host)
    {
        host = string.Empty;
        if (text.Length is 0 or > 253 || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (text.Any(c => c is '[' or ']' or '%'))
        {
            return false;
        }

        if (IPAddress.TryParse(text, out var literal))
        {
            if (HasScope(literal) || literal.IsIPv4MappedToIPv6)
            {
                return false;
            }

            host = literal.ToString();
            return true;
        }

        if (text.Any(c => c is '/' or ':' or '*' or '@' or '?' or '#' or '[' or ']' or '\\' || c < 0x21 || c == 0x7F)
            || text.StartsWith('.') || text.EndsWith('.'))
        {
            return false;
        }

        try
        {
            var ascii = new IdnMapping().GetAscii(text).ToLowerInvariant();
            if (ascii.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'
                    || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                return false;
            }

            host = ascii;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
