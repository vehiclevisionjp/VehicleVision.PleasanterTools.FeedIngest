using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.FeedIngest.Cli.Ingest;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
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
        + "  VehicleVision.PleasanterTools.FeedIngest migrate | migrate-status";

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

    private static async Task<int> CheckAsync(TextWriter output, TextWriter error, Func<string, string?>? environment)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var httpClient = new HttpClient(handler);
            var client = new PleasanterApiClient(httpClient, PleasanterOptions.FromEnvironment(environment));
            var connected = await client.CheckConnectionAsync().ConfigureAwait(false);
            output.WriteLine(connected ? "Connected." : "Connection failed.");
            return connected ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
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

        FeedIngestConfiguration configuration;
        try
        {
            configuration = ConfigurationReader.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
        }
        catch (ConfigurationException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // パスやシステムのメッセージは出さない
            error.WriteLine("Cannot read the configuration file.");
            return 2;
        }

        try
        {
            using var feedHandler = FeedFetcher.CreateDefaultHandler();
            using var pleasanterHandler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var pleasanterHttp = new HttpClient(pleasanterHandler);
            ExternalKeyUpserter? upserter = null;
            if (!dryRun)
            {
                var options = new PleasanterOptions
                {
                    BaseUrl = configuration.Pleasanter.BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    ApiKey = configuration.Pleasanter.ApiKey.Resolve(environment),
                    ApiVersion = configuration.Pleasanter.ApiVersion,
                };
                upserter = new ExternalKeyUpserter(new PleasanterApiClient(pleasanterHttp, options));
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
                output);
            var report = await runner.RunAsync(dryRun, token).ConfigureAwait(false);
            return report.ExitCode;
        }
        catch (InvalidOperationException ex)
        {
            // 秘密参照の解決失敗など。キー名以外の秘密内容は含まれない
            error.WriteLine(ex.Message);
            return 2;
        }
    }
}
