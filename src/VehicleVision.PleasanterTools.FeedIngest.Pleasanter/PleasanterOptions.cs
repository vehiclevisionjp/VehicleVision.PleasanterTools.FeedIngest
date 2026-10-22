using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

/// <summary>Pleasanter の接続設定を呼び出し時に解決する。</summary>
public interface IPleasanterOptionsProvider
{
    Task<PleasanterOptions> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>接続先 Pleasanter の設定。</summary>
/// <remarks>
/// 実値は環境変数か Git 管理外の設定ファイルから読む。
/// **API キーを設定ファイルへ直書きしてコミットしないこと。**
/// </remarks>
public sealed class PleasanterOptions
{
    /// <summary>API のバージョンの既定。</summary>
    public const decimal DefaultApiVersion = 1.1m;

    /// <summary>1 回の呼び出しの上限の既定。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>ベース URL。末尾のスラッシュは付けない。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>API キー。**サーバ側だけが持つ。ブラウザへ渡さない。**</summary>
    public required string ApiKey { get; init; }

    /// <summary>API のバージョン。リクエストボディの <c>ApiVersion</c> に載せる。</summary>
    public decimal ApiVersion { get; init; } = DefaultApiVersion;

    /// <summary>1 回の呼び出しの上限。</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>環境変数 <c>PLEASANTER_BASE_URL</c> / <c>PLEASANTER_API_KEY</c> から作る。</summary>
    public static PleasanterOptions FromEnvironment(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var legacy = new[] { "PLEASANTER_BASE_URL", "PLEASANTER_API_KEY", "PLEASANTER_API_KEY_FILE" };
        var current = new[]
        {
            "FEEDINGEST_PLEASANTER_BASE_URL", "FEEDINGEST_PLEASANTER_API_KEY",
            "FEEDINGEST_PLEASANTER_API_KEY_FILE",
        };
        if (legacy.Any(name => environment(name) is not null)
            && current.Any(name => environment(name) is not null))
        {
            throw new InvalidOperationException("PLEASANTER_* と FEEDINGEST_PLEASANTER_* は併用できません。");
        }

        var prefix = current.Any(name => environment(name) is not null) ? "FEEDINGEST_PLEASANTER_" : "PLEASANTER_";
        var url = environment(prefix + "BASE_URL");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || uri.Query.Length != 0)
        {
            throw new InvalidOperationException($"環境変数 {prefix}BASE_URL は資格情報を含まない HTTPS URL が必要です。");
        }

        return new PleasanterOptions
        {
            BaseUrl = url!,
            ApiKey = new SecretReference(prefix + "API_KEY").Resolve(environment),
        };
    }
}

/// <summary>固定した Pleasanter 接続設定を返す。</summary>
public sealed class StaticPleasanterOptionsProvider(PleasanterOptions options)
    : IPleasanterOptionsProvider
{
    public Task<PleasanterOptions> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(options);
}
