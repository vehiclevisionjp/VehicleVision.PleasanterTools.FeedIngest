using System.Data.Common;
using Dapper;
using VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;
using VehicleVision.PleasanterTools.FeedIngest.Core.State;

namespace VehicleVision.PleasanterTools.FeedIngest.Data;

/// <summary>取り込み状態を RDBMS へ保管する。</summary>
/// <remarks>
/// <para>SQL は <see cref="SqlDialect.Format"/> を通して書く（識別子は角括弧で囲む）。方言差を持ち込まない。
/// 日時は <see cref="DbTime.ForDb"/> で書き、<see cref="DbTime.AsUtc(DateTime)"/> で読む。</para>
/// <para>原文 XML・API キー・認証の値・Pleasanter の応答本体は保存しない。</para>
/// <para>初期は 1 台・SQLite の単発 CLI を前提とする（他の RDBMS・複数ホストは未検証）。</para>
/// </remarks>
public sealed class IngestStateStore(IDbConnectionFactory connections, TimeProvider? time = null) : IIngestStateStore
{
    private const string ActiveToken = "active";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    private DateTime Now() => DbTime.ForDb(_time.GetUtcNow().UtcDateTime);

    private static DateTime? ForDb(DateTimeOffset? value) => value is { } v ? DbTime.ForDb(v.UtcDateTime) : null;

    private static DateTimeOffset? FromDb(DateTime? value) => DbTime.AsUtc(value) is { } v ? new DateTimeOffset(v) : null;

    // 取得状態

    public async Task<SourceState?> GetSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<SourceRow>(new CommandDefinition(
            Sql("SELECT [SourceId], [ConfigVersion], [ETag], [LastModified], [FetchedAt] FROM [Sources] WHERE [SourceId] = @SourceId"),
            new { SourceId = sourceId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null
            ? null
            : new SourceState(row.SourceId, row.ConfigVersion, row.ETag, FromDb(row.LastModified), FromDb(row.FetchedAt));
    }

    public async Task CommitFetchAsync(FetchCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            foreach (var entry in commit.Entries)
            {
                await UpsertEntryAsync(connection, transaction, commit.SourceId, entry, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            await UpsertSourceAsync(connection, transaction, commit, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 記事も検証子も、何も確定しない（次回の 200 で取り直す）
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task UpsertEntryAsync(
        DbConnection connection, DbTransaction transaction, string sourceId, Core.NormalizedEntry entry, DateTime now,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(entry.SourceId, sourceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("記事の取り込み元が、確定の取り込み元と一致しません。");
        }

        var key = ExternalKey.For(entry);
        var json = EntrySnapshot.ToJson(entry);
        var hash = EntrySnapshot.HashOf(json);

        var existing = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            Sql("SELECT [ContentHash] FROM [Entries] WHERE [SourceId] = @SourceId AND [ExternalKey] = @ExternalKey"),
            new { SourceId = sourceId, ExternalKey = key },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (existing is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [Entries] ([Id], [SourceId], [ExternalKey], [ContentHash], [EntryJson], [CreatedAt], [UpdatedAt]) "
                    + "VALUES (@Id, @SourceId, @ExternalKey, @ContentHash, @EntryJson, @Now, @Now)"),
                new { Id = Guid.NewGuid().ToString("D"), SourceId = sourceId, ExternalKey = key, ContentHash = hash, EntryJson = json, Now = now },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        else if (!string.Equals(existing, hash, StringComparison.Ordinal))
        {
            // 内容が変わったときだけ更新する（同じ内容では UpdatedAt も動かさない）
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("UPDATE [Entries] SET [ContentHash] = @ContentHash, [EntryJson] = @EntryJson, [UpdatedAt] = @Now "
                    + "WHERE [SourceId] = @SourceId AND [ExternalKey] = @ExternalKey"),
                new { SourceId = sourceId, ExternalKey = key, ContentHash = hash, EntryJson = json, Now = now },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task UpsertSourceAsync(
        DbConnection connection, DbTransaction transaction, FetchCommit commit, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            commit.SourceId,
            commit.ConfigVersion,
            commit.ETag,
            LastModified = ForDb(commit.LastModified),
            FetchedAt = ForDb(commit.FetchedAt),
        };
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Sources] SET [ConfigVersion] = @ConfigVersion, [ETag] = @ETag, [LastModified] = @LastModified, "
                + "[FetchedAt] = @FetchedAt WHERE [SourceId] = @SourceId"),
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (updated == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [Sources] ([SourceId], [ConfigVersion], [ETag], [LastModified], [FetchedAt]) "
                    + "VALUES (@SourceId, @ConfigVersion, @ETag, @LastModified, @FetchedAt)"),
                parameters,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    public async Task ClearValidatorsAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Sources] SET [ETag] = NULL, [LastModified] = NULL WHERE [SourceId] = @SourceId"),
            new { SourceId = sourceId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredEntry>> ListEntriesAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<EntryRow>(new CommandDefinition(
            Sql("SELECT [SourceId], [ExternalKey], [ContentHash], [EntryJson], [UpdatedAt] FROM [Entries] "
                + "WHERE [SourceId] = @SourceId ORDER BY [CreatedAt], [ExternalKey]"),
            new { SourceId = sourceId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows
            .Select(row => new StoredEntry(
                row.SourceId,
                row.ExternalKey,
                EntrySnapshot.FromJson(row.EntryJson),
                row.ContentHash,
                new DateTimeOffset(DbTime.AsUtc(row.UpdatedAt))))
            .ToList();
    }

    // 配送

    public async Task<Delivery?> GetDeliveryAsync(
        string destinationId, long siteId, string externalKey, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<DeliveryRow>(new CommandDefinition(
            Sql("SELECT [DestinationId], [SiteId], [SourceId], [ExternalKey], [RecordId], [MappingVersion], [ValueHash], "
                + "[State], [AttemptCount], [LastOutcome], [UpdatedAt] FROM [Deliveries] "
                + "WHERE [DestinationId] = @DestinationId AND [SiteId] = @SiteId AND [ExternalKey] = @ExternalKey"),
            new { DestinationId = destinationId, SiteId = siteId, ExternalKey = externalKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToDelivery();
    }

    public async Task SaveDeliveryAsync(DeliverySave save, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var now = Now();
        var parameters = new
        {
            save.DestinationId,
            save.SiteId,
            save.SourceId,
            save.ExternalKey,
            save.RecordId,
            save.MappingVersion,
            save.ValueHash,
            State = save.State.ToString(),
            Attempt = save.CountAttempt ? 1 : 0,
            save.LastOutcome,
            Now = now,
        };
        await using var connection = connections.Create();
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Deliveries] SET [SourceId] = @SourceId, [RecordId] = COALESCE(@RecordId, [RecordId]), "
                + "[MappingVersion] = @MappingVersion, [ValueHash] = @ValueHash, [State] = @State, "
                + "[AttemptCount] = [AttemptCount] + @Attempt, [LastOutcome] = @LastOutcome, [UpdatedAt] = @Now "
                + "WHERE [DestinationId] = @DestinationId AND [SiteId] = @SiteId AND [ExternalKey] = @ExternalKey"),
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (updated > 0)
        {
            return;
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [Deliveries] ([Id], [DestinationId], [SiteId], [SourceId], [ExternalKey], [RecordId], "
                    + "[MappingVersion], [ValueHash], [State], [AttemptCount], [LastOutcome], [CreatedAt], [UpdatedAt]) "
                    + "VALUES (@Id, @DestinationId, @SiteId, @SourceId, @ExternalKey, @RecordId, @MappingVersion, @ValueHash, "
                    + "@State, @Attempt, @LastOutcome, @Now, @Now)"),
                new
                {
                    Id = Guid.NewGuid().ToString("D"),
                    parameters.DestinationId,
                    parameters.SiteId,
                    parameters.SourceId,
                    parameters.ExternalKey,
                    parameters.RecordId,
                    parameters.MappingVersion,
                    parameters.ValueHash,
                    parameters.State,
                    parameters.Attempt,
                    parameters.LastOutcome,
                    parameters.Now,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 一意制約で競合した（他が先に作った）なら更新し直す。それ以外の失敗は伝える
            if (await GetDeliveryAsync(save.DestinationId, save.SiteId, save.ExternalKey, cancellationToken).ConfigureAwait(false) is null)
            {
                throw;
            }

            await SaveDeliveryAsync(save, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<int> MarkInterruptedAsUnknownAsync(string destinationId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Deliveries] SET [State] = @Unknown, [UpdatedAt] = @Now "
                + "WHERE [DestinationId] = @DestinationId AND [State] = @Sending"),
            new
            {
                DestinationId = destinationId,
                Unknown = nameof(DeliveryState.Unknown),
                Sending = nameof(DeliveryState.Sending),
                Now = Now(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // 実行の排他

    public async Task<RunLease?> TryBeginRunAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = Now();
        try
        {
            await using var connection = connections.Create();
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [Runs] ([Id], [OwnerId], [ActiveToken], [Status], [StartedAt]) "
                    + "VALUES (@Id, @OwnerId, @ActiveToken, 'Running', @Now)"),
                new { Id = id.ToString("D"), OwnerId = ownerId, ActiveToken, Now = now },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ActiveToken の一意制約: 他の実行が終わっていない。引き継がない
            if (await FindActiveRunAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }

            throw;
        }

        return new RunLease(id, ownerId, new DateTimeOffset(DbTime.AsUtc(now)));
    }

    public async Task EndRunAsync(Guid runId, string status, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Runs] SET [ActiveToken] = @Id, [Status] = @Status, [FinishedAt] = @Now "
                + "WHERE [Id] = @Id AND [ActiveToken] = @ActiveToken"),
            new { Id = runId.ToString("D"), Status = status, Now = Now(), ActiveToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<RunLease?> FindActiveRunAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            Sql("SELECT [Id], [OwnerId], [StartedAt] FROM [Runs] WHERE [ActiveToken] = @ActiveToken"),
            new { ActiveToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null
            ? null
            : new RunLease(Guid.Parse(row.Id), row.OwnerId, new DateTimeOffset(DbTime.AsUtc(row.StartedAt)));
    }

    public async Task<bool> ForceEndActiveRunAsync(CancellationToken cancellationToken = default)
    {
        var active = await FindActiveRunAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return false;
        }

        await EndRunAsync(active.Id, "ForceEnded", cancellationToken).ConfigureAwait(false);
        return true;
    }

    // SQLite は record のコンストラクタ（名前つき）を文字列・整数から戻せないため、行はプロパティの単純なクラスで受ける
    private sealed class SourceRow
    {
        public string SourceId { get; set; } = string.Empty;

        public string ConfigVersion { get; set; } = string.Empty;

        public string? ETag { get; set; }

        public DateTime? LastModified { get; set; }

        public DateTime? FetchedAt { get; set; }
    }

    private sealed class EntryRow
    {
        public string SourceId { get; set; } = string.Empty;

        public string ExternalKey { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public string EntryJson { get; set; } = string.Empty;

        public DateTime UpdatedAt { get; set; }
    }

    private sealed class DeliveryRow
    {
        public string DestinationId { get; set; } = string.Empty;

        public long SiteId { get; set; }

        public string SourceId { get; set; } = string.Empty;

        public string ExternalKey { get; set; } = string.Empty;

        public long? RecordId { get; set; }

        public string? MappingVersion { get; set; }

        public string? ValueHash { get; set; }

        public string State { get; set; } = string.Empty;

        public int AttemptCount { get; set; }

        public string? LastOutcome { get; set; }

        public DateTime UpdatedAt { get; set; }

        public Delivery ToDelivery() => new(
            DestinationId,
            SiteId,
            SourceId,
            ExternalKey,
            RecordId,
            MappingVersion,
            ValueHash,
            Enum.Parse<DeliveryState>(State),
            AttemptCount,
            LastOutcome,
            new DateTimeOffset(DbTime.AsUtc(UpdatedAt)));
    }

    private sealed class RunRow
    {
        public string Id { get; set; } = string.Empty;

        public string OwnerId { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; }
    }
}
