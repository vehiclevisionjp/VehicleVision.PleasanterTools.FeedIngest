using System.Net;
using System.Net.Sockets;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>
/// 取得先として許す IP アドレスか判定する（SSRF 対策）。loopback・link-local・プライベート宛などを拒否する。
/// </summary>
/// <remarks>
/// <see cref="IsAllowed"/> は「許可リストなし」の判定（公開アドレスだけ許す）。内部ホストの明示許可（U11）は
/// <see cref="DestinationPolicy"/>（source ごとの許可リスト）が、このクラスの公開アドレス判定に上乗せして行う。
/// </remarks>
public static class DestinationGuard
{
    public static bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(bytes),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address, bytes),
            _ => false,
        };
    }

    private static bool IsPublicIPv4(byte[] b)
        => !(b[0] == 0                                  // 0.0.0.0/8
            || b[0] == 10                               // 10.0.0.0/8
            || b[0] == 100 && (b[1] & 0xC0) == 64       // 100.64.0.0/10（共有アドレス空間）
            || b[0] == 127                              // loopback
            || b[0] == 169 && b[1] == 254               // link-local（クラウドのメタデータを含む）
            || b[0] == 172 && (b[1] & 0xF0) == 16       // 172.16.0.0/12
            || b[0] == 192 && b[1] == 0 && b[2] == 0    // 192.0.0.0/24
            || b[0] == 192 && b[1] == 168               // 192.168.0.0/16
            || b[0] == 198 && (b[1] & 0xFE) == 18       // 198.18.0.0/15（試験用）
            || b[0] >= 224);                            // マルチキャスト・予約・ブロードキャスト

    private static bool IsPublicIPv6(IPAddress address, byte[] b)
        => !(address.IsIPv6LinkLocal                    // fe80::/10
            || address.IsIPv6SiteLocal                  // fec0::/10
            || address.IsIPv6Multicast                  // ff00::/8
            || address.IsIPv6UniqueLocal                // fc00::/7
            || address.IsIPv6Teredo
            || b[0] == 0x20 && b[1] == 0x02             // 6to4（2002::/16。埋め込み IPv4 の検査を避けて拒否）
            || b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B  // 64:ff9b::/96（NAT64）
            || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8);  // 2001:db8::/32

    /// <summary>名前を解決し、全アドレスが許可されるときだけ返す。1 つでも不許可なら拒否する。</summary>
    public static Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver,
        CancellationToken cancellationToken)
        => ResolveAllowedAsync(host, resolver, DestinationPolicy.Default, cancellationToken);

    /// <summary>名前を解決し、全アドレスが <paramref name="policy"/> で許可されるときだけ返す。1 つでも不許可なら拒否する。</summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver,
        DestinationPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                throw new FeedFetchException(FeedFetchErrorKind.Transport, null, "名前を解決できません。");
            }
        }

        if (addresses.Length == 0)
        {
            throw new FeedFetchException(FeedFetchErrorKind.Transport, null, "名前を解決できません。");
        }

        if (addresses.Any(address => !policy.IsAllowed(host, address)))
        {
            throw new FeedFetchException(
                FeedFetchErrorKind.Destination, null, "取得先が許可されないアドレス（内部・予約）を指しています。");
        }

        return addresses;
    }
}
