using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

/// <summary>環境変数から Pleasanter の接続設定を作る（<c>check</c> 用）。</summary>
/// <remarks>
/// 接続先の検査は DotNetClient が通信の前にも行うが、ここでも HTTPS のみを要求する
/// （DotNetClient はループバックの <c>http</c> も許すが、本ツールは許さない）。
/// </remarks>
public static class PleasanterEnvironment
{
    /// <summary>環境変数 <c>PLEASANTER_BASE_URL</c> / <c>PLEASANTER_API_KEY</c>（または <c>FEEDINGEST_PLEASANTER_*</c>）から作る。</summary>
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
            DetectServerVersion = false,
        };
    }
}
