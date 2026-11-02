using Microsoft.Data.Sqlite;
using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.FeedIngest.Core;
using VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;
using VehicleVision.PleasanterTools.FeedIngest.Core.State;
using VehicleVision.PleasanterTools.FeedIngest.Data;

namespace VehicleVision.PleasanterTools.FeedIngest.Data.Tests;

/// <summary>SQLite の一時 DB（実 DB）での、取り込み状態の試験。</summary>
public sealed class IngestStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "feedingest-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;

    public IngestStateStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "feedingest.db")}";
        FeedIngestDatabase.Instance.Migrator.MigrateUp(DatabaseProvider.Sqlite, _connectionString);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 一時ファイル。残っても試験結果に影響しない
        }
    }

    private IngestStateStore NewStore(TimeProvider? time = null)
        => new(new DbConnectionFactory(DatabaseProvider.Sqlite, _connectionString), time);

    private static NormalizedEntry Entry(string id = "id-1", string title = "題名", string source = "src")
        => new(source, id, EntryIdentityKind.NativeId, new Uri("https://news.example.test/" + id), title, "要約", "<p>本文</p>",
            EntryContentKind.Html, new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.FromHours(9)), null,
            [new EntryCategory("t", "s", "L")], [new EntryAuthor("Ann", "a@example.test", new Uri("https://ann.example.test"))],
            [new EntryEnclosure(new Uri("https://cdn.example.test/a.mp3"), "audio/mpeg", 12)]);

    private static FetchCommit Commit(string source = "src", string version = "v1", string? etag = "W/\"abc\"",
        DateTimeOffset? lastModified = null, params NormalizedEntry[] entries)
        => new(source, version, etag, lastModified, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), entries);

    private static DeliverySave Save(string key, DeliveryState state = DeliveryState.Succeeded, string? hash = "h1",
        string? mapping = "m1", long? record = 7, bool attempt = true, string destination = "office", long site = 1)
        => new(destination, site, "src", key, state, record, mapping, hash, "Created", attempt);

    [Fact]
    public void アプリ_ID_から名前が導かれ_マイグレーションが適用される()
    {
        Assert.Equal("feedingest", FeedIngestDatabase.Instance.AppId);
        Assert.Equal("feedingest.db", FeedIngestDatabase.Instance.DefaultSqliteFileName);
        Assert.Equal("FEEDINGEST_DB_PROVIDER", FeedIngestDatabase.Instance.ProviderSetting);
        Assert.False(FeedIngestDatabase.Instance.Migrator.HasPendingMigrations(DatabaseProvider.Sqlite, _connectionString));
    }

    // 取得状態・スナップショット

    [Fact]
    public async Task 取得の確定は記事と検証子を保存し_スナップショットを正確に復元する()
    {
        var store = NewStore();
        var entry = Entry();
        var modified = new DateTimeOffset(2026, 10, 20, 1, 2, 3, TimeSpan.Zero);

        await store.CommitFetchAsync(Commit(lastModified: modified, entries: entry));

        var source = await store.GetSourceAsync("src");
        Assert.Equal("W/\"abc\"", source!.ETag);
        Assert.Equal(modified, source.LastModified);
        Assert.Equal("v1", source.ConfigVersion);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), source.FetchedAt);
        var stored = Assert.Single(await store.ListEntriesAsync("src"));
        Assert.Equal(ExternalKey.For(entry), stored.ExternalKey);
        Assert.Equal(EntrySnapshot.ToJson(entry), EntrySnapshot.ToJson(stored.Entry));
        Assert.Equal("要約", stored.Entry.Summary);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), stored.Entry.PublishedAt);
        Assert.Equal("audio/mpeg", Assert.Single(stored.Entry.Enclosures).Type);
    }

    [Fact]
    public async Task 取得の確定に失敗したら記事も検証子も確定しない()
    {
        var store = NewStore();
        await store.CommitFetchAsync(Commit(etag: "\"old\"", entries: Entry("a")));
        // 2 件目の取り込み元が違う記事は、確定の途中で失敗する
        var broken = Commit(etag: "\"new\"", entries: [Entry("b"), Entry("c", source: "other")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitFetchAsync(broken));

        var source = await store.GetSourceAsync("src");
        Assert.Equal("\"old\"", source!.ETag);
        Assert.Equal(["a"], (await store.ListEntriesAsync("src")).Select(e => e.Entry.NativeId));
    }

    [Fact]
    public async Task 同じ内容の記事は更新せず_変わった内容だけ最新へ置き換える()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        await store.CommitFetchAsync(Commit(entries: Entry()));
        var first = (await store.ListEntriesAsync("src")).Single();

        clock.Advance(TimeSpan.FromHours(1));
        await store.CommitFetchAsync(Commit(entries: Entry()));
        var same = (await store.ListEntriesAsync("src")).Single();
        clock.Advance(TimeSpan.FromHours(1));
        await store.CommitFetchAsync(Commit(entries: Entry(title: "更新後")));
        var changed = (await store.ListEntriesAsync("src")).Single();

        Assert.Equal(first.UpdatedAt, same.UpdatedAt);
        Assert.Equal(first.ContentHash, same.ContentHash);
        Assert.NotEqual(first.ContentHash, changed.ContentHash);
        Assert.Equal("更新後", changed.Entry.Title);
        Assert.True(changed.UpdatedAt > first.UpdatedAt);
    }

    [Fact]
    public async Task 記事は取り込み元ごとに区切り_フィードから消えた記事を削除しない()
    {
        var store = NewStore();
        await store.CommitFetchAsync(Commit(entries: [Entry("a"), Entry("b")]));
        await store.CommitFetchAsync(Commit("other", entries: Entry("a", source: "other")));

        await store.CommitFetchAsync(Commit(entries: Entry("a")));

        Assert.Equal(["a", "b"], (await store.ListEntriesAsync("src")).Select(e => e.Entry.NativeId).Order());
        Assert.Single(await store.ListEntriesAsync("other"));
    }

    [Fact]
    public async Task 検証子を捨てても記事と版は残る()
    {
        var store = NewStore();
        await store.CommitFetchAsync(Commit(lastModified: DateTimeOffset.UnixEpoch, entries: Entry()));

        await store.ClearValidatorsAsync("src");

        var source = await store.GetSourceAsync("src");
        Assert.Null(source!.ETag);
        Assert.Null(source.LastModified);
        Assert.Equal("v1", source.ConfigVersion);
        Assert.Single(await store.ListEntriesAsync("src"));
        Assert.Null(await store.GetSourceAsync("unknown"));
    }

    [Fact]
    public async Task 検証子の無い取得の確定は古い検証子を引き継がない()
    {
        var store = NewStore();
        await store.CommitFetchAsync(Commit(etag: "\"old\"", lastModified: DateTimeOffset.UnixEpoch, entries: Entry()));

        await store.CommitFetchAsync(Commit(etag: null, lastModified: null, entries: Entry()));

        var source = await store.GetSourceAsync("src");
        Assert.Null(source!.ETag);
        Assert.Null(source.LastModified);
    }

    // 配送

    [Fact]
    public async Task 配送は作成と更新で同じ行を使い_試行回数と結果の分類を残す()
    {
        var store = NewStore();

        await store.SaveDeliveryAsync(Save("k1", DeliveryState.Sending, hash: null, mapping: null, record: null));
        await store.SaveDeliveryAsync(Save("k1", DeliveryState.Succeeded, hash: "h2", record: 42));
        await store.SaveDeliveryAsync(Save("k1", DeliveryState.Held, hash: "h2", record: null, attempt: false));

        var delivery = await store.GetDeliveryAsync("office", 1, "k1");
        Assert.Equal(DeliveryState.Held, delivery!.State);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Equal(42, delivery.RecordId);
        Assert.Equal("h2", delivery.ValueHash);
        Assert.Equal("m1", delivery.MappingVersion);
        Assert.Equal("Created", delivery.LastOutcome);
    }

    [Fact]
    public async Task 配送は送り先_サイト_外部キーで区切る()
    {
        var store = NewStore();

        await store.SaveDeliveryAsync(Save("k1"));
        await store.SaveDeliveryAsync(Save("k1", destination: "other"));
        await store.SaveDeliveryAsync(Save("k1", site: 2));

        Assert.NotNull(await store.GetDeliveryAsync("office", 1, "k1"));
        Assert.NotNull(await store.GetDeliveryAsync("other", 1, "k1"));
        Assert.NotNull(await store.GetDeliveryAsync("office", 2, "k1"));
        Assert.Null(await store.GetDeliveryAsync("office", 3, "k1"));
    }

    [Fact]
    public async Task 配送の一意制約を直接の二重挿入は許さない()
    {
        await NewStore().SaveDeliveryAsync(Save("k1"));
        await using var connection = new DbConnectionFactory(DatabaseProvider.Sqlite, _connectionString).Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Deliveries (Id, DestinationId, SiteId, SourceId, ExternalKey, State, AttemptCount, CreatedAt, UpdatedAt) "
            + "VALUES ('x', 'office', 1, 'src', 'k1', 'Pending', 0, '2026-11-02', '2026-11-02')";

        await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task 送信中のまま残った配送は_Unknown_にし_他の送り先と他の状態は触らない()
    {
        var store = NewStore();
        await store.SaveDeliveryAsync(Save("sending", DeliveryState.Sending));
        await store.SaveDeliveryAsync(Save("done", DeliveryState.Succeeded));
        await store.SaveDeliveryAsync(Save("other-dest", DeliveryState.Sending, destination: "other"));

        var changed = await store.MarkInterruptedAsUnknownAsync("office");

        Assert.Equal(1, changed);
        Assert.Equal(DeliveryState.Unknown, (await store.GetDeliveryAsync("office", 1, "sending"))!.State);
        Assert.Equal(DeliveryState.Succeeded, (await store.GetDeliveryAsync("office", 1, "done"))!.State);
        Assert.Equal(DeliveryState.Sending, (await store.GetDeliveryAsync("other", 1, "other-dest"))!.State);
    }

    // 実行排他

    [Fact]
    public async Task 実行の排他は1つだけ取れ_終了すれば取り直せる()
    {
        var store = NewStore();

        var first = await store.TryBeginRunAsync("owner-1");
        var second = await store.TryBeginRunAsync("owner-2");

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal("owner-1", (await store.FindActiveRunAsync())!.OwnerId);

        await store.EndRunAsync(first.Id, "Succeeded");
        var third = await store.TryBeginRunAsync("owner-3");

        Assert.NotNull(third);
        Assert.Null(await NewStore().TryBeginRunAsync("owner-4"));
    }

    [Fact]
    public async Task 別のストアや同時の取得でも排他は1つだけ()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => NewStore().TryBeginRunAsync("owner-" + i)));

        Assert.Equal(1, results.Count(lease => lease is not null));
    }

    [Fact]
    public async Task 異常終了で残った排他は明示操作で解除できる()
    {
        var store = NewStore();
        Assert.False(await store.ForceEndActiveRunAsync());
        await store.TryBeginRunAsync("crashed");

        Assert.True(await store.ForceEndActiveRunAsync());

        Assert.Null(await store.FindActiveRunAsync());
        Assert.NotNull(await store.TryBeginRunAsync("next"));
    }

    [Fact]
    public async Task 他の実行の終了は排他に影響しない()
    {
        var store = NewStore();
        var first = await store.TryBeginRunAsync("a");
        await store.EndRunAsync(first!.Id, "Succeeded");
        var second = await store.TryBeginRunAsync("b");

        await store.EndRunAsync(first.Id, "Succeeded");

        Assert.Equal(second!.Id, (await store.FindActiveRunAsync())!.Id);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan span) => _now += span;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
