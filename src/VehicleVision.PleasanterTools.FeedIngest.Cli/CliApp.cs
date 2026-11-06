using System.Reflection;
using System.Text.Json;
using VehicleVision.PleasanterTools.Core.Cli;
using VehicleVision.PleasanterTools.Core.Contracts;
using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.FeedIngest.Cli.Ingest;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
using VehicleVision.PleasanterTools.FeedIngest.Core.State;
using VehicleVision.PleasanterTools.FeedIngest.Data;
using VehicleVision.PleasanterTools.FeedIngest.Feeds;
using VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

namespace VehicleVision.PleasanterTools.FeedIngest.Cli;

/// <summary>
/// コマンドラインの入口。引数の解析・ヘルプ・エラー・<c>--version</c>・補完・Ctrl+C は Core.Cli（CLI の共通仕様）に任せる。
/// 終了コードは 0＝成功、1＝失敗（一部でも保留・失敗・未処理・排他を取れなかった場合を含む）、2＝使い方・設定の誤り、130＝中断。
/// </summary>
public static class CliApp
{
    /// <summary>ツール名（<c>--version</c> の表示・ヘルプ・環境変数の接頭辞 <c>FEEDINGEST_</c> の元）。</summary>
    public const string ToolName = "feedingest";

    public const string CheckCommand = "check";
    public const string IngestCommand = "ingest";
    public const string MigrateCommand = "migrate";
    public const string MigrateStatusCommand = "migrate-status";
    public const string UnlockCommand = "unlock";
    public const string ValidateCommand = "validate";

    // 固有の ErrorCodes（--json の errors[].code）
    private const string ConnectionFailedCode = "ConnectionFailed";
    private const string ConfigurationInvalidCode = "ConfigurationInvalid";
    private const string ConfigurationUnreadableCode = "ConfigurationUnreadable";
    private const string SecretUnresolvedCode = "SecretUnresolved";
    private const string StateDatabaseNotMigratedCode = "StateDatabaseNotMigrated";
    private const string StateDatabaseFailedCode = "StateDatabaseFailed";
    private const string MigrationsPendingCode = "MigrationsPending";
    private const string RunStoppedCode = "RunStopped";
    private const string SourceErrorCode = "SourceError";
    private const string SourceNotProcessedCode = "SourceNotProcessed";
    private const string EntriesIncompleteCode = "EntriesIncomplete";

    private static readonly OptionSpec ConfigOption = new()
    {
        Name = "config",
        Description = "Path of the configuration file.",
        Kind = OptionKind.Value,
        ValueName = "FILE",
        Required = true,
        ValueCompletion = OptionValueCompletion.File,
    };

    private static readonly OptionSpec DryRunOption = new()
    {
        Name = "dry-run",
        Description = "Fetch, parse and map only. Connect to neither Pleasanter nor the state database.",
    };

    private static readonly OptionSpec CheckSecretsOption = new()
    {
        Name = "check-secrets",
        Description = "Also check that the secrets the configuration refers to (environment variables, _FILE) can be resolved. Only variable names are reported.",
    };

    private static readonly OptionSpec ResendUnconfirmedOption = new()
    {
        Name = "resend-unconfirmed",
        Description = "Allow Create for deliveries whose earlier create result is unconfirmed. Use only after confirming the request was not applied.",
    };

    public static ToolSpec Tool { get; } = new(
        ToolName,
        "Fetch RSS / Atom feeds and create or update Pleasanter records.",
        ToolVersion(),
        [
            new CommandSpec(CheckCommand, "Check the connection to Pleasanter.", [StandardOptions.Json]),
            new CommandSpec(IngestCommand, "Ingest the feeds described in a configuration file.", [ConfigOption, DryRunOption, ResendUnconfirmedOption, StandardOptions.Json]),
            new CommandSpec(ValidateCommand, "Validate a configuration file. No connection is made and the state database is not touched.", [ConfigOption, CheckSecretsOption, StandardOptions.Json]),
            new CommandSpec(MigrateCommand, "Apply the pending migrations to the state database.", []),
            new CommandSpec(MigrateStatusCommand, "Show the pending migrations (exit code 1 if any).", [StandardOptions.Json]),
            new CommandSpec(UnlockCommand, "Release a run lock left by an abnormal exit (only when no other run is active).", []),
            CompletionCommand.Spec,
        ]);

    public static IReadOnlyDictionary<string, CommandHandler> Handlers { get; } = new Dictionary<string, CommandHandler>
    {
        [CheckCommand] = CheckAsync,
        [IngestCommand] = IngestAsync,
        [ValidateCommand] = ValidateAsync,
        [MigrateCommand] = (context, token) => MigrateAsync(context, MigrationCommand.ApplyArgument, token),
        [MigrateStatusCommand] = MigrateStatusAsync,
        [UnlockCommand] = UnlockAsync,
        [CompletionCommand.Name] = CompletionCommand.CreateHandler(Tool),
    };

    /// <summary>コンソールと環境変数で実行する（<c>Main</c> から）。</summary>
    public static Task<int> RunWithConsoleAsync(string[] args) =>
        CliApplication.RunWithConsoleAsync(Tool, Handlers, args);

    /// <summary>出力・入力・環境変数を差し替えて実行する（試験用）。</summary>
    public static Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, Func<string, string?>? environment, CancellationToken cancellationToken) =>
        CliApplication.RunAsync(
            Tool, Handlers, args, output, error, TextReader.Null, environment ?? Environment.GetEnvironmentVariable, cancellationToken);

    private static string ToolVersion()
    {
        var informational = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0];
        return string.IsNullOrEmpty(version) ? "0.0.0" : version;
    }

    /// <summary>失敗を <c>[ERROR]</c>（stderr）で出し、<c>--json</c> なら枠も stdout へ出して、終了コードを返す。</summary>
    private static int Fail(CommandContext context, string command, bool json, int exitCode, string code, string message)
    {
        context.Output.Error(message);
        if (json)
        {
            context.Output.WriteJson(command, false, exitCode, [new CliIssue(code, message)], []);
        }

        return exitCode;
    }

    private static async Task<int> CheckAsync(CommandContext context, CancellationToken token)
    {
        var json = context.Options.Flag(StandardOptions.JsonName);
        try
        {
            // 環境変数の欠落・不正は UsageException（Core.Cli が [ERROR] と 2 にする）
            using var client = PleasanterGateway.Create(PleasanterEnvironment.FromEnvironment(context.GetEnvironmentVariable));
            var connected = await client.CheckConnectionAsync(token).ConfigureAwait(false);
            if (!connected)
            {
                context.Output.Error("Connection failed.");
            }
            else if (!json)
            {
                context.Output.Ok("Connected.");
            }

            if (json)
            {
                context.Output.WriteJson(
                    CheckCommand,
                    connected,
                    connected ? ExitCodeValues.Success : ExitCodeValues.Failure,
                    connected ? [] : [new CliIssue(ConnectionFailedCode, "Connection failed.")],
                    [],
                    writer => writer.WriteBoolean("connected", connected));
            }

            return connected ? ExitCodeValues.Success : ExitCodeValues.Failure;
        }
        catch (ArgumentException ex)
        {
            // DotNetClient の BaseUrl 検査。文言に API キーも URL も含まれない
            return Fail(context, CheckCommand, json, ExitCodeValues.UsageError, ErrorCodes.Usage, ex.Message);
        }
    }

    /// <summary>状態 DB のマイグレーションを当てる。</summary>
    private static async Task<int> MigrateAsync(CommandContext context, string argument, CancellationToken token)
    {
        try
        {
            using var output = new CliOutputWriter(context.Output, toStderr: false);
            using var error = new CliOutputWriter(context.Output, toStderr: true);
            return await DatabaseSetup
                .MigrateAsync(argument, context.GetEnvironmentVariable, Directory.GetCurrentDirectory(), output, error, token)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // 設定の誤り。接続文字列などの値は含まれない
            return Fail(context, MigrateCommand, false, ExitCodeValues.UsageError, ErrorCodes.Usage, ex.Message);
        }
        catch (System.Data.Common.DbException)
        {
            return Fail(context, MigrateCommand, false, ExitCodeValues.Failure, StateDatabaseFailedCode, "The state database failed.");
        }
    }

    /// <summary>未適用のマイグレーションを見る。未適用があれば 1。</summary>
    private static async Task<int> MigrateStatusAsync(CommandContext context, CancellationToken token)
    {
        var json = context.Options.Flag(StandardOptions.JsonName);
        if (!json)
        {
            try
            {
                using var output = new CliOutputWriter(context.Output, toStderr: false);
                using var error = new CliOutputWriter(context.Output, toStderr: true);
                return await DatabaseSetup
                    .MigrateAsync(MigrationCommand.StatusArgument, context.GetEnvironmentVariable, Directory.GetCurrentDirectory(), output, error, token)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Fail(context, MigrateStatusCommand, false, ExitCodeValues.UsageError, ErrorCodes.Usage, ex.Message);
            }
            catch (System.Data.Common.DbException)
            {
                return Fail(context, MigrateStatusCommand, false, ExitCodeValues.Failure, StateDatabaseFailedCode, "The state database failed.");
            }
        }

        try
        {
            var database = DatabaseSetup.Resolve(context.GetEnvironmentVariable, Directory.GetCurrentDirectory());
            var pending = FeedIngestDatabase.Instance.Migrator.PendingMigrations(database.Provider, database.ConnectionString);
            var ok = pending.Count == 0;
            context.Output.WriteJson(
                MigrateStatusCommand,
                ok,
                ok ? ExitCodeValues.Success : ExitCodeValues.Failure,
                ok ? [] : [new CliIssue(MigrationsPendingCode, $"{pending.Count} migration(s) are pending.")],
                [],
                writer =>
                {
                    writer.WriteString("provider", database.Provider.ToString());
                    writer.WriteStartArray("pending");
                    foreach (var name in pending)
                    {
                        writer.WriteStringValue(name);
                    }

                    writer.WriteEndArray();
                });
            return ok ? ExitCodeValues.Success : ExitCodeValues.Failure;
        }
        catch (InvalidOperationException ex)
        {
            return Fail(context, MigrateStatusCommand, true, ExitCodeValues.UsageError, ErrorCodes.Usage, ex.Message);
        }
        catch (System.Data.Common.DbException)
        {
            return Fail(context, MigrateStatusCommand, true, ExitCodeValues.Failure, StateDatabaseFailedCode, "The state database failed.");
        }
    }

    /// <summary>
    /// 異常終了で残った実行の排他を、運用者の明示操作で解除する。
    /// **他の実行が本当に動いていないことを確認してから使う**（動いている最中に解除すると二重に実行され得る）。
    /// </summary>
    private static async Task<int> UnlockAsync(CommandContext context, CancellationToken token)
    {
        try
        {
            var database = DatabaseSetup.Resolve(context.GetEnvironmentVariable, Directory.GetCurrentDirectory());
            if (FeedIngestDatabase.Instance.Migrator.HasPendingMigrations(database.Provider, database.ConnectionString))
            {
                return Fail(
                    context, UnlockCommand, false, ExitCodeValues.UsageError, StateDatabaseNotMigratedCode,
                    "The state database is not up to date. Run 'migrate' first.");
            }

            var store = new IngestStateStore(new DbConnectionFactory(database.Provider, database.ConnectionString));
            context.Output.Ok(await store.ForceEndActiveRunAsync(token).ConfigureAwait(false)
                ? "Released the active run lock."
                : "No active run lock.");
            return ExitCodeValues.Success;
        }
        catch (InvalidOperationException ex)
        {
            return Fail(context, UnlockCommand, false, ExitCodeValues.UsageError, ErrorCodes.Usage, ex.Message);
        }
        catch (System.Data.Common.DbException)
        {
            return Fail(context, UnlockCommand, false, ExitCodeValues.Failure, StateDatabaseFailedCode, "The state database failed.");
        }
    }

    /// <summary>
    /// 設定ファイルを読んで検証する。誤りなら <c>Invalid</c>（行とキーつき）、読めないなら <c>Unreadable</c>（パスやシステムの文言は含めない）に入れる。
    /// <c>ingest</c> と <c>validate</c> が同じ読み方をする。
    /// </summary>
    private static async Task<(FeedIngestConfiguration? Configuration, ConfigurationException? Invalid, string? Unreadable)> ReadConfigurationAsync(
        string path, CancellationToken token)
    {
        try
        {
            return (ConfigurationReader.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false)), null, null);
        }
        catch (ConfigurationException ex)
        {
            return (null, ex, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, null, "Cannot read the configuration file.");
        }
    }

    /// <summary>設定が参照する秘密（API キー・認証の利用者名・パスワード・ヘッダー値）。</summary>
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

    /// <summary>
    /// 設定ファイルを通信せずに検証する（状態 DB にも Pleasanter にも触れない）。見つかった問題は結果なので stdout へ出す。
    /// 検査を始められない（ファイルが読めない）は 2、始められて問題ありは 1、問題なしは 0。
    /// <c>--check-secrets</c> は、参照する秘密が解決できることだけを確かめる（環境変数名だけ報告し、値・パスは出さない）。
    /// </summary>
    private static async Task<int> ValidateAsync(CommandContext context, CancellationToken token)
    {
        var json = context.Options.Flag(StandardOptions.JsonName);
        var checkSecrets = context.Options.Flag(CheckSecretsOption.Name);
        var path = context.Options.Value(ConfigOption.Name)!;
        var (configuration, invalid, unreadable) = await ReadConfigurationAsync(path, token).ConfigureAwait(false);
        if (unreadable is not null)
        {
            return Fail(context, ValidateCommand, json, ExitCodeValues.UsageError, ConfigurationUnreadableCode, unreadable);
        }

        var issues = new List<CliIssue>();
        if (invalid is not null)
        {
            issues.Add(new CliIssue(ConfigurationInvalidCode, invalid.Message, invalid.Key));
        }
        else if (checkSecrets)
        {
            foreach (var secret in SecretsOf(configuration!))
            {
                try
                {
                    _ = secret.Resolve(context.GetEnvironmentVariable);
                }
                catch (InvalidOperationException ex)
                {
                    // 文言にはキー名（環境変数名）だけが含まれる
                    issues.Add(new CliIssue(SecretUnresolvedCode, ex.Message));
                }
            }
        }

        var exitCode = issues.Count > 0 ? ExitCodeValues.Failure : ExitCodeValues.Success;
        if (json)
        {
            context.Output.WriteJson(
                ValidateCommand,
                exitCode == ExitCodeValues.Success,
                exitCode,
                issues,
                [],
                writer =>
                {
                    writer.WriteStartObject("checked");
                    if (configuration is null)
                    {
                        writer.WriteNull("sources");
                    }
                    else
                    {
                        writer.WriteNumber("sources", configuration.Sources.Count);
                    }

                    writer.WriteBoolean("secrets", checkSecrets && invalid is null);
                    writer.WriteEndObject();
                });
            return exitCode;
        }

        foreach (var issue in issues)
        {
            context.Output.Result($"error [{issue.Code}] {issue.Message}");
        }

        if (issues.Count > 0)
        {
            context.Output.Result($"Configuration is invalid ({issues.Count} error(s)).");
            return exitCode;
        }

        foreach (var source in configuration!.Sources)
        {
            context.Output.Result($"source={source.Id} site={source.Target.SiteId} mode={source.Target.Mode} mappings={source.Mapping.Count}");
        }

        context.Output.Ok($"Configuration is valid ({configuration.Sources.Count} source(s)).{(checkSecrets ? " Secrets are resolved." : string.Empty)} No connection was made.");
        return exitCode;
    }

    /// <summary>
    /// 内部ホストの許可リスト（U11）が設定されていれば、警告を 1 行だけ出す。許可の範囲・ホスト名は出さず、件数だけにする。
    /// </summary>
    private static void WarnAllowedDestinations(FeedIngestConfiguration configuration, CommandContext context)
    {
        var sources = configuration.Sources
            .Where(source => source.Http.AllowedNetworks.Count > 0 || source.Http.AllowedHosts.Count > 0)
            .ToList();
        if (sources.Count == 0)
        {
            return;
        }

        context.Output.Warn(
            $"{sources.Count} source(s) allow internal destinations "
            + $"({sources.Sum(source => source.Http.AllowedNetworks.Count)} network(s), "
            + $"{sources.Sum(source => source.Http.AllowedHosts.Count)} host(s)). Keep the lists minimal.");
    }

    private static async Task<int> IngestAsync(CommandContext context, CancellationToken token)
    {
        var json = context.Options.Flag(StandardOptions.JsonName);
        var dryRun = context.Options.Flag(DryRunOption.Name);
        var resendUnconfirmed = context.Options.Flag(ResendUnconfirmedOption.Name);
        var path = context.Options.Value(ConfigOption.Name)!;
        var environment = context.GetEnvironmentVariable;
        if (dryRun && resendUnconfirmed)
        {
            return Fail(
                context, IngestCommand, json, ExitCodeValues.UsageError, ErrorCodes.Usage,
                "--resend-unconfirmed cannot be combined with --dry-run. Try --help for usage.");
        }

        var (configuration, invalid, unreadable) = await ReadConfigurationAsync(path, token).ConfigureAwait(false);
        if (configuration is null)
        {
            return invalid is not null
                ? Fail(context, IngestCommand, json, ExitCodeValues.UsageError, ConfigurationInvalidCode, invalid.Message)
                : Fail(context, IngestCommand, json, ExitCodeValues.UsageError, ConfigurationUnreadableCode, unreadable!);
        }

        PleasanterGateway? gateway = null;
        try
        {
            WarnAllowedDestinations(configuration, context);
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
                var database = DatabaseSetup.Resolve(environment, Directory.GetCurrentDirectory());
                if (FeedIngestDatabase.Instance.Migrator.HasPendingMigrations(database.Provider, database.ConnectionString))
                {
                    return Fail(
                        context, IngestCommand, json, ExitCodeValues.UsageError, StateDatabaseNotMigratedCode,
                        "The state database is not up to date. Run 'migrate' first.");
                }

                stateStore = new IngestStateStore(new DbConnectionFactory(database.Provider, database.ConnectionString));
            }

            var runner = new IngestRunner(
                configuration,
                // source ごとに、その source の許可リストを結び付けた取得器を作る（許可は他の source へ効かない）
                source => FeedFetcher.CreateDefault(
                    new FeedFetchOptions
                    {
                        RequestTimeout = TimeSpan.FromSeconds(source.Http.TimeoutSeconds),
                        MaxRedirects = source.Http.MaxRedirects,
                        MaxBodyBytes = source.Http.MaxBodyBytes,
                    },
                    DestinationPolicy.From(source.Http)),
                upserter,
                environment,
                json ? null : context.Output,
                stateStore,
                $"{Environment.ProcessId}@{Environment.MachineName}",
                resendUnconfirmed: resendUnconfirmed);
            var report = await runner.RunAsync(dryRun, token).ConfigureAwait(false);
            return Report(context, report, dryRun, json);
        }
        catch (System.Data.Common.DbException)
        {
            // 状態 DB の障害。送信前に止まる。接続文字列・SQL は出さない
            return Fail(context, IngestCommand, json, ExitCodeValues.Failure, StateDatabaseFailedCode, "The state database failed.");
        }
        catch (InvalidOperationException ex)
        {
            // 秘密参照の解決失敗など。キー名以外の秘密内容は含まれない
            return Fail(context, IngestCommand, json, ExitCodeValues.UsageError, ConfigurationInvalidCode, ex.Message);
        }
        finally
        {
            gateway?.Dispose();
        }
    }

    /// <summary>実行の結果を、末尾の <c>[OK]</c>／<c>[ERROR]</c>（テキスト）または枠つきの JSON で出す。</summary>
    private static int Report(CommandContext context, IngestReport report, bool dryRun, bool json)
    {
        var exitCode = report.ExitCode;
        if (exitCode != ExitCodeValues.Success)
        {
            // --json でも、同じ内容を stderr に出す（stdout は JSON だけ）
            context.Output.Error(json
                ? "Not all sources were completed. See errors in the JSON result."
                : "Not all sources were completed. See the result lines above.");
        }
        else if (!json)
        {
            context.Output.Ok(dryRun ? "Dry run completed." : "Ingest completed.");
        }

        if (!json)
        {
            return exitCode;
        }

        var errors = new List<CliIssue>();
        if (report.StoppedBy is not null)
        {
            errors.Add(new CliIssue(RunStoppedCode, $"The run was stopped: {report.StoppedBy}."));
        }

        foreach (var source in report.Sources)
        {
            if (source.NotProcessed)
            {
                errors.Add(new CliIssue(SourceNotProcessedCode, $"Source '{source.SourceId}' was not processed."));
                continue;
            }

            if (source.Error is not null)
            {
                errors.Add(new CliIssue(SourceErrorCode, $"Source '{source.SourceId}' failed: {source.Error}."));
            }

            if (source.Held > 0 || source.Failed > 0 || source.Pending > 0)
            {
                errors.Add(new CliIssue(
                    EntriesIncompleteCode,
                    $"Source '{source.SourceId}' has held={source.Held} failed={source.Failed} pending={source.Pending} entries."));
            }
        }

        context.Output.WriteJson(
            IngestCommand,
            exitCode == ExitCodeValues.Success,
            exitCode,
            errors,
            [],
            writer =>
            {
                writer.WriteBoolean("dryRun", dryRun);
                if (report.StoppedBy is null)
                {
                    writer.WriteNull("stoppedReason");
                }
                else
                {
                    writer.WriteString("stoppedReason", report.StoppedBy);
                }

                writer.WriteStartArray("sources");
                foreach (var source in report.Sources)
                {
                    WriteSource(writer, source);
                }

                writer.WriteEndArray();
            });
        return exitCode;
    }

    private static void WriteSource(Utf8JsonWriter writer, SourceReport source)
    {
        writer.WriteStartObject();
        writer.WriteString("id", source.SourceId);
        writer.WriteBoolean("notProcessed", source.NotProcessed);
        writer.WriteNumber("created", source.Created);
        writer.WriteNumber("updated", source.Updated);
        writer.WriteNumber("unchanged", source.Unchanged);
        writer.WriteNumber("held", source.Held);
        writer.WriteNumber("unconfirmed", source.Unconfirmed);
        writer.WriteNumber("failed", source.Failed);
        writer.WriteNumber("pending", source.Pending);
        writer.WriteNumber("mapped", source.Mapped);
        if (source.Error is null)
        {
            writer.WriteNull("error");
        }
        else
        {
            writer.WriteString("error", source.Error);
        }

        writer.WriteEndObject();
    }
}
