using System.Net;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>
/// 取得先として許す宛先の方針（U11）。source ごとに 1 つ作り、その source の取得だけに使う（他の source へ漏らさない）。
/// </summary>
/// <remarks>
/// <para>公開アドレスは常に許す。内部・予約アドレス（loopback・プライベートなど）は、
/// <b>ホスト名（URL のホスト）が <c>allowedHosts</c> に載り、かつ解決後の IP が <c>allowedNetworks</c> に入る</b>ときだけ許す
/// （どちらか一方では許さない。厳しい側の解釈）。</para>
/// <para>link-local（メタデータを含む）・マルチキャスト・予約・IPv4 を埋め込む IPv6 の変換形式は、設定に関係なく常に拒否する
/// （<see cref="DestinationRules.IsNeverAllowed"/>）。</para>
/// <para>既定（<see cref="Default"/>）は許可リストが空で、従来どおり内部・予約アドレスを常に拒否する。</para>
/// </remarks>
public sealed class DestinationPolicy
{
    private readonly IReadOnlyList<IPNetwork> _networks;
    private readonly HashSet<string> _hosts;

    public DestinationPolicy(IEnumerable<IPNetwork> allowedNetworks, IEnumerable<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedNetworks);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        _networks = allowedNetworks.ToArray();
        _hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in allowedHosts)
        {
            if (!DestinationRules.TryNormalizeHost(host, out var normalized))
            {
                throw new ArgumentException("許可するホストの形式が不正です。", nameof(allowedHosts));
            }

            _hosts.Add(normalized);
        }
    }

    /// <summary>許可リストなし。内部・予約アドレス宛を常に拒否する。</summary>
    public static DestinationPolicy Default { get; } = new([], []);

    public static DestinationPolicy From(HttpConfiguration http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.AllowedNetworks.Count == 0 && http.AllowedHosts.Count == 0
            ? Default
            : new DestinationPolicy(http.AllowedNetworks, http.AllowedHosts);
    }

    /// <summary><paramref name="host"/>（URL のホスト）へ解決された <paramref name="address"/> へ接続してよいか。</summary>
    public bool IsAllowed(string host, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(address);
        address = DestinationRules.Normalize(address);
        if (DestinationRules.IsNeverAllowed(address))
        {
            return false;
        }

        if (DestinationGuard.IsAllowed(address))
        {
            return true;
        }

        if (_networks.Count == 0 || _hosts.Count == 0
            || !DestinationRules.TryNormalizeHost(host.Trim('[', ']'), out var normalized)
            || !_hosts.Contains(normalized))
        {
            return false;
        }

        return _networks.Any(network => network.Contains(address));
    }
}
