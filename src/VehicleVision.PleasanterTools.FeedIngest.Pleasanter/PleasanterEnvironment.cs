using VehicleVision.PleasanterTools.Core.Cli;
using VehicleVision.PleasanterTools.Core.Contracts;
using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

/// <summary>環境変数から Pleasanter の接続設定を作る（<c>check</c> 用）。</summary>
/// <remarks>
/// <para>名前は製品群で共通の <c>VVPT_PLEASANTER_*</c>（<see cref="PleasanterEnvironmentNames"/>）。
/// 旧名（<c>PLEASANTER_*</c>・<c>FEEDINGEST_PLEASANTER_*</c>）は読まない（無視する。検出も案内もしない）。
/// 旧名だけが設定された環境は、新名が未設定として扱われる。
/// DotNetClient へ渡す値は、ここで読んで <see cref="PleasanterOptions"/> に入れる（DotNetClient の環境変数の読み取りは使わない）。</para>
/// <para>接続先の検査は DotNetClient が通信の前にも行うが、ここでも HTTPS のみを要求する
/// （DotNetClient はループバックの <c>http</c> も許すが、本ツールは許さない）。</para>
/// </remarks>
public static class PleasanterEnvironment
{
    /// <summary>環境変数 <c>VVPT_PLEASANTER_BASE_URL</c> と、<c>VVPT_PLEASANTER_API_KEY</c>（または <c>VVPT_PLEASANTER_API_KEY_FILE</c>）から作る。</summary>
    /// <exception cref="UsageException">未設定・不正（メッセージに API キーの値・URL の値を含めない）。</exception>
    public static PleasanterOptions FromEnvironment(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var url = environment(PleasanterEnvironmentNames.BaseUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || uri.Query.Length != 0)
        {
            throw new UsageException(
                $"Environment variable {PleasanterEnvironmentNames.BaseUrl} must be an HTTPS URL without credentials, query or fragment.");
        }

        var apiKey = PleasanterApiKey.Resolve(environment)
            ?? throw new UsageException(
                $"Set {PleasanterEnvironmentNames.ApiKey} or {PleasanterEnvironmentNames.ApiKeyFile}.");

        return new PleasanterOptions
        {
            BaseUrl = url!,
            ApiKey = apiKey,
            DetectServerVersion = false,
        };
    }
}
