using FluentMigrator;

namespace VehicleVision.PleasanterTools.FeedIngest.Data.Migrations;

/// <summary>取得状態・記事のスナップショット・配送・実行排他の表（設計 アーキテクチャ方針 6 章）。</summary>
/// <remarks>
/// <para>**4 種の RDBMS で通じる型だけを使う。** 状態は文字列、日時はネイティブ日時型、本文 JSON は最大長の文字列。</para>
/// <para>記事のスナップショット（本文・enclosure の URL）は秘密相当として、DB のアクセスを制限する。
/// 原文 XML・API キー・認証の値・Pleasanter の応答本体は保存しない。</para>
/// <para>設計の「Attempts」表は作らない。試行回数と直近の結果の分類は Deliveries の列で持つ。</para>
/// </remarks>
[Migration(1, "取り込み状態の表")]
public sealed class M0001_IngestState : Migration
{
    public override void Up()
    {
        // 取り込み元ごとの取得状態。検証子は、解析・保存に成功してから進める
        Create.Table("Sources")
            .WithColumn("SourceId").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("ConfigVersion").AsString(64).NotNullable()
            .WithColumn("ETag").AsString(400).Nullable()
            .WithColumn("LastModified").AsDateTime2().Nullable()
            .WithColumn("FetchedAt").AsDateTime2().Nullable();

        // 正規化済みの記事（最新の内容のスナップショット）
        Create.Table("Entries")
            .WithColumn("Id").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("SourceId").AsString(64).NotNullable()
            .WithColumn("ExternalKey").AsString(80).NotNullable()
            .WithColumn("ContentHash").AsString(64).NotNullable()
            .WithColumn("EntryJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();
        Create.Index("UX_Entries_Source_Key").OnTable("Entries")
            .OnColumn("SourceId").Ascending()
            .OnColumn("ExternalKey").Ascending()
            .WithOptions().Unique();

        // 配送の記録。同じ記事を同じ送り先・サイトへ二重に確保させない
        Create.Table("Deliveries")
            .WithColumn("Id").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("DestinationId").AsString(64).NotNullable()
            .WithColumn("SiteId").AsInt64().NotNullable()
            .WithColumn("SourceId").AsString(64).NotNullable()
            .WithColumn("ExternalKey").AsString(80).NotNullable()
            .WithColumn("RecordId").AsInt64().Nullable()
            .WithColumn("MappingVersion").AsString(64).Nullable()
            .WithColumn("ValueHash").AsString(64).Nullable()
            .WithColumn("State").AsString(16).NotNullable()
            .WithColumn("AttemptCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("LastOutcome").AsString(64).Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();
        Create.Index("UX_Deliveries_Key").OnTable("Deliveries")
            .OnColumn("DestinationId").Ascending()
            .OnColumn("SiteId").Ascending()
            .OnColumn("ExternalKey").Ascending()
            .WithOptions().Unique();
        Create.Index("IX_Deliveries_State").OnTable("Deliveries")
            .OnColumn("DestinationId").Ascending()
            .OnColumn("State").Ascending();

        // 取り込み実行の排他。ActiveToken は実行中だけ 'active'、終了後は実行 ID。
        // 実行中の行を ActiveToken の一意制約で 1 つに絞る（NULL の扱いが RDBMS で違うため NULL を使わない）。
        // マイグレーションの排他（Core.Data の DatabaseMigrationLock）とは別物
        Create.Table("Runs")
            .WithColumn("Id").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("OwnerId").AsString(64).NotNullable()
            .WithColumn("ActiveToken").AsString(36).NotNullable()
            .WithColumn("Status").AsString(32).NotNullable()
            .WithColumn("StartedAt").AsDateTime2().NotNullable()
            .WithColumn("FinishedAt").AsDateTime2().Nullable();
        Create.Index("UX_Runs_Active").OnTable("Runs")
            .OnColumn("ActiveToken").Ascending()
            .WithOptions().Unique();

        // ⚠️ MySQL は文字列の列が utf8mb3 で作られる（AtomFeed M0001 と同じ理由）。表ごと utf8mb4 へ直す
        foreach (var table in new[] { "Sources", "Entries", "Deliveries", "Runs" })
        {
            IfDatabase("MySql").Execute.Sql(
                $"ALTER TABLE `{table}` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;");
        }
    }

    public override void Down()
    {
        Delete.Table("Runs");
        Delete.Table("Deliveries");
        Delete.Table("Entries");
        Delete.Table("Sources");
    }
}
