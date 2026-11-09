using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.FeedIngest.Cli.Ingest;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
using VehicleVision.PleasanterTools.FeedIngest.Core.State;
using VehicleVision.PleasanterTools.FeedIngest.Data;
using VehicleVision.PleasanterTools.FeedIngest.Feeds;
using VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

namespace VehicleVision.PleasanterTools.FeedIngest.Cli;

/// <summary>コマンドラインの入口。終了コードは 0＝成功、1＝一部または全部が未完了、2＝使い方・設定の誤り、130＝中断。</summary>
public static class CliApp
{
    public const string Usage =
        "Usage:\n"
        + "  VehicleVision.PleasanterTools.FeedIngest check\n"
        + "  VehicleVision.PleasanterTools.FeedIngest ingest --config <file> [--dry-run]\n"
        + "  VehicleVision.PleasanterTools.FeedIngest migrate | migrate-status\n"
        + "  VehicleVision.PleasanterTools.FeedIngest unlock";

    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, Func<string, string?>? environment, CancellationToken cancellationToken)
    {
        try
        {
            return await DispatchAsync(args, output, error, environment, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 送信中だった書き込みは結果不明。次回の実行が外部キーで照合する
            error.WriteLine("Cancelled.");
            return 130;
        }
    }

    private static async Task<int> DispatchAsync(
        string[] args, TextWriter output, TextWriter error, Func<string, string?>? environment, CancellationToken cancellationToken)
    {
        if (args.Length == 1 && args[0] == "check")
        {
            return await CheckAsync(output, error, environment).ConfigureAwait(false);
        }

        if (args.Length == 1 && args[0] is "migrate" or "migrate-status")
        {
            return await MigrateAsync(args[0], error, environment, cancellationToken).ConfigureAwait(false);
        }

        if (args.Length == 1 && args[0] == "unlock")
        {
            return await UnlockAsync(output, error, environment, cancellationToken).ConfigureAwait(false);
        }

        if (args.Length >= 1 && args[0] == "validate")
        {
            return await ValidateAsync(args, output, error, environment, cancellationToken).ConfigureAwait(false);
        }

        if (args.Length >= 1 && args[0] == "ingest")
        {
            return await IngestAsync(args, output, error, environment, cancellationToken).ConfigureAwait(false);
        }

        error.WriteLine(Usage);
        return 2;
    }

    /// <summary>状態 DB のマイグレーションを当てる（migrate）／未適用を見る（migrate-status。未適用があれば 1）。</summary>
    private static async Task<int> MigrateAsync(
        string command, TextWriter error, Func<string, string?>? environment, CancellationToken token)
    {
        try
        {
            return await DatabaseSetup
                .MigrateAsync(
                    command == "migrate" ? MigrationCommand.ApplyArgument : MigrationCommand.StatusArgument,
                    environment ?? Environment.GetEnvironmentVariable,
                    Directory.GetCurrentDirectory(),
                    token)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // 設定の誤り。接続文字列などの値は含まれない
            error.WriteLine(ex.Message);
            return 2;
        }
    }

    /// <summary>
    /// 異常終了で残った実行の排他を、運用者の明示操作で解除する。
    /// **他の実行が本当に動いていないことを確認してから使う**（動いている最中に解除すると二重に実行され得る）。
    /// </summary>
    private static async Task<int> UnlockAsync(
        TextWriter output, TextWriter error, Func<string, string?>? environment, CancellationToken token)
    {
        try
        {
            var database = DatabaseSetup.Resolve(environment ?? Environment.GetEnvironmentVariable, Directory.GetCurrentDirectory());
            if (FeedIngestDatabase.Instance.Migrator.HasPendingMigrations(database.Provider, database.ConnectionString))
            {
                error.WriteLine("The state database is not up to date. Run 'migrate' first.");
                return 2;
            }

            var store = new IngestStateStore(new DbConnectionFactory(database.Provider, database.ConnectionString));
            output.WriteLine(await store.ForceEndActiveRunAsync(token).ConfigureAwait(false)
                ? "Released the active run lock."
                : "No active run lock.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
        catch (System.Data.Common.DbException)
        {
            error.WriteLine("The state database failed.");
            return 1;
        }
    }

    private static async Task<int> CheckAsync(TextWriter output, TextWriter error, Func<string, string?>? environment)
    {
        try
        {
            using var client = PleasanterGateway.Create(PleasanterEnvironment.FromEnvironment(environment));
            var connected = await client.CheckConnectionAsync().ConfigureAwait(false);
            output.WriteLine(connected ? "Connected." : "Connection failed.");
            return connected ? 0 : 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // 環境変数が無い・不正（InvalidOperationException）か、DotNetClient の BaseUrl 検査（ArgumentException）。
            // 文言に API キーも URL も含まれない
            error.WriteLine(ex.Message);
            return 2;
        }
    }

    /// <summary>設定ファイルを読んで検証する。誤りなら診断を <paramref name="error"/> へ書いて <c>null</c>（パスや値は出さない）。</summary>
    private static async Task<FeedIngestConfiguration?> ReadConfigurationAsync(string path, TextWriter error, CancellationToken token)
    {
        try
        {
            return ConfigurationReader.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
        }
        catch (ConfigurationException ex)
        {
            error.WriteLine(ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // パスやシステムのメッセージは出さない
            error.WriteLine("Cannot read the configuration file.");
            return null;
        }
    }

    /// <summary>
    /// 設定ファイルを通信せずに検証する（状態 DB にも Pleasanter にも触れない）。
    /// <c>--check-secrets</c> は、参照する秘密が解決できることだけを確かめる（環境変数名だけ報告し、値・パスは出さない）。
    /// </summary>
    private static async Task<int> ValidateAsync(
        string[] args, TextWriter output, TextWriter error, Func<string, string?>? environment, CancellationToken token)
    {
        string? path = null;
        var checkSecrets = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length && path is null)
            {
                path = args[++i];
            }
            else if (args[i] == "--check-secrets" && !checkSecrets)
            {
                checkSecrets = true;
            }
            else
            {
                error.WriteLine(Usage);
                return 2;
            }
        }

        if (path is null)
        {
            error.WriteLine(Usage);
            return 2;
        }

        var configuration = await ReadConfigurationAsync(path, error, token).ConfigureAwait(false);
        if (configuration is null)
        {
            return 2;
        }

        if (checkSecrets)
        {
            var failed = false;
            foreach (var secret in SecretsOf(configuration))
            {
                try
                {
                    _ = secret.Resolve(environment);
                }
                catch (InvalidOperationException ex)
                {
                    // 文言にキー名（環境変数名）だけが含まれる
                    error.WriteLine(ex.Message);
                    failed = true;
                }
            }

            if (failed)
            {
                return 2;
            }
        }

        output.WriteLine($"valid pleasanter={configuration.Pleasanter.Id} sources={configuration.Sources.Count}"
            + (checkSecrets ? " secrets=resolved" : string.Empty));
        foreach (var source in configuration.Sources)
        {
            output.WriteLine($"source={source.Id} site={source.Target.SiteId} mode={source.Target.Mode} mappings={source.Mapping.Count}");
        }

        return 0;
    }

    private static IEnumerable<SecretReference> SecretsOf(FeedIngestConfiguration configuration)
    {
        yield return configuration.Pleasanter.ApiKey;
        foreach (var authentication in configuration.Sources.Select(source => source.Http.Authentication))
        {
            if (authentication.Username is not null)
            {
                yield return authentication.Username;
            }

            if (authentication.Password is not null)
            {
                yield return authentication.Password;
            }

            foreach (var header in authentication.Headers)
            {
                yield return header.Value;
            }
        }
    }

    private static async Task<int> IngestAsync(
        string[] args, TextWriter output, TextWriter error, Func<string, string?>? environment, CancellationToken token)
    {
        string? path = null;
        var dryRun = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length && path is null)
            {
                path = args[++i];
            }
            else if (args[i] == "--dry-run" && !dryRun)
            {
                dryRun = true;
            }
            else
            {
                error.WriteLine(Usage);
                return 2;
            }
        }

        if (path is null)
        {
            error.WriteLine(Usage);
            return 2;
        }

        var configuration = await ReadConfigurationAsync(path, error, token).ConfigureAwait(false);
        if (configuration is null)
        {
            return 2;
        }

        PleasanterGateway? gateway = null;
        try
        {
            using var feedHandler = FeedFetcher.CreateDefaultHandler();
            ExternalKeyUpserter? upserter = null;
            IIngestStateStore? stateStore = null;
            if (!dryRun)
            {
                var options = new PleasanterOptions
                {
                    BaseUrl = configuration.Pleasanter.BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    ApiKey = configuration.Pleasanter.ApiKey.Resolve(environment),
                    ApiVersion = configuration.Pleasanter.ApiVersion,
                    DetectServerVersion = false,
                };
                gateway = PleasanterGateway.Create(options);
                upserter = new ExternalKeyUpserter(gateway);

                // 状態 DB（取得状態・配送・実行排他）。マイグレーション済みであること
                var database = DatabaseSetup.Resolve(environment ?? Environment.GetEnvironmentVariable, Directory.GetCurrentDirectory());
                if (FeedIngestDatabase.Instance.Migrator.HasPendingMigrations(database.Provider, database.ConnectionString))
                {
                    error.WriteLine("The state database is not up to date. Run 'migrate' first.");
                    return 2;
                }

                stateStore = new IngestStateStore(new DbConnectionFactory(database.Provider, database.ConnectionString));
            }

            var runner = new IngestRunner(
                configuration,
                source => new FeedFetcher(
                    feedHandler,
                    new FeedFetchOptions
                    {
                        RequestTimeout = TimeSpan.FromSeconds(source.Http.TimeoutSeconds),
                        MaxRedirects = source.Http.MaxRedirects,
                        MaxBodyBytes = source.Http.MaxBodyBytes,
                    }),
                upserter,
                environment,
                output,
                stateStore,
                $"{Environment.ProcessId}@{Environment.MachineName}");
            var report = await runner.RunAsync(dryRun, token).ConfigureAwait(false);
            return report.ExitCode;
        }
        catch (System.Data.Common.DbException)
        {
            // 状態 DB の障害。送信前に止まる。接続文字列・SQL は出さない
            error.WriteLine("The state database failed.");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            // 秘密参照の解決失敗など。キー名以外の秘密内容は含まれない
            error.WriteLine(ex.Message);
            return 2;
        }
        finally
        {
            gateway?.Dispose();
        }
    }
}
