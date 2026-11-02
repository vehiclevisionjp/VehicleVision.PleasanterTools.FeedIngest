using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.FeedIngest.Data;

namespace VehicleVision.PleasanterTools.FeedIngest.Cli;

/// <summary>状態 DB の接続先の解決。名前（設定名・既定の SQLite ファイル名）は Core.Data がアプリ ID から導く。</summary>
/// <remarks>
/// 設定: <c>FEEDINGEST_DB_PROVIDER</c>（既定は sqlite）、<c>FEEDINGEST_DB_CONNECTIONSTRING</c>、
/// <c>FEEDINGEST_DB_ALLOW_INSECURE</c>。接続文字列は設定ファイルへ書かず、環境変数から読む。
/// SQLite の既定は <c>App_Data/feedingest.db</c>（カレントディレクトリ配下）。
/// </remarks>
public static class DatabaseSetup
{
    /// <exception cref="InvalidOperationException">設定が不正（値は含めない）。</exception>
    public static DatabaseConnectionInfo Resolve(Func<string, string?> environment, string contentRoot)
    {
        var database = FeedIngestDatabase.Instance;
        var provider = database.ParseProvider(environment(database.ProviderSetting), DatabaseProvider.Sqlite);
        var connectionString = database.ResolveConnectionString(
            provider, environment(database.ConnectionStringSetting), contentRoot);
        database.EnsureSecure(
            provider,
            connectionString,
            string.Equals(environment(database.AllowInsecureSetting), "true", StringComparison.OrdinalIgnoreCase));
        return new DatabaseConnectionInfo(provider, connectionString);
    }

    public static async Task<int> MigrateAsync(
        string argument, Func<string, string?> environment, string contentRoot, CancellationToken cancellationToken)
    {
        var info = Resolve(environment, contentRoot);
        return await MigrationCommand
            .RunAsync(FeedIngestDatabase.Instance.Migrator, info.Provider, info.ConnectionString, [argument], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
