using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

/// <summary>検証済みの秘密なし設定から、要求時だけ API キーを解決する。</summary>
public sealed class ConfiguredPleasanterOptionsProvider(
    DestinationConfiguration configuration, Func<string, string?>? environment = null)
    : IPleasanterOptionsProvider
{
    public Task<PleasanterOptions> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PleasanterOptions
        {
            BaseUrl = configuration.BaseUrl.AbsoluteUri,
            ApiVersion = configuration.ApiVersion,
            ApiKey = configuration.ApiKey.Resolve(environment),
            // 版の問い合わせの通信をしない（API 呼び出しの予算 UpsertOptions.MaxRequests の外で通信を増やさない）
            DetectServerVersion = false,
        });
    }
}
