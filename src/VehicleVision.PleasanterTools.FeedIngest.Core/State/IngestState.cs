namespace VehicleVision.PleasanterTools.FeedIngest.Core.State;

/// <summary>取り込み元の取得状態。検証子は、解析・保存に成功してから進める。</summary>
/// <param name="SourceId">取り込み元の安定 ID。</param>
/// <param name="ConfigVersion">URL・認証設定の版（秘密そのものは含めない）。変わったら検証子は使わない。</param>
/// <param name="ETag">引用符・弱いタグを含めたまま保存した ETag。</param>
/// <param name="LastModified">Last-Modified。</param>
/// <param name="FetchedAt">最後に取得・保存に成功した時刻。</param>
public sealed record SourceState(
    string SourceId, string ConfigVersion, string? ETag, DateTimeOffset? LastModified, DateTimeOffset? FetchedAt);

/// <summary>保存済みの記事（正規化後のスナップショット）。</summary>
/// <param name="SourceId">取り込み元の ID。</param>
/// <param name="ExternalKey">外部キー（<see cref="Mapping.ExternalKey"/>）。</param>
/// <param name="Entry">正規化済みの記事。</param>
/// <param name="ContentHash">内容のハッシュ（正規化記事の決定的な JSON の SHA-256）。</param>
/// <param name="UpdatedAt">内容が最後に変わった時刻。</param>
public sealed record StoredEntry(
    string SourceId, string ExternalKey, NormalizedEntry Entry, string ContentHash, DateTimeOffset UpdatedAt);

/// <summary>
/// 取得の確定。記事のスナップショットと検証子を、同じトランザクションで保存する。
/// </summary>
/// <remarks>
/// 200 を受けただけでは確定しない。本文全体の検証・解析・正規化に成功した結果だけを渡す。
/// 保存に失敗したら、旧い検証子が残り、次回の 200 で取り直す。
/// </remarks>
public sealed record FetchCommit(
    string SourceId,
    string ConfigVersion,
    string? ETag,
    DateTimeOffset? LastModified,
    DateTimeOffset FetchedAt,
    IReadOnlyList<NormalizedEntry> Entries);

/// <summary>1 記事の送り先への配送の状態。</summary>
public enum DeliveryState
{
    /// <summary>送る予定。</summary>
    Pending,

    /// <summary>送信中。プロセスが止まって残った場合は <see cref="Unknown"/> として扱う。</summary>
    Sending,

    /// <summary>送信を確認し、記録した。</summary>
    Succeeded,

    /// <summary>結果が分からない（中断・通信断）。外部キーで照合してから判断する。</summary>
    Unknown,

    /// <summary>人の判断まで保留。</summary>
    Held,
}

/// <summary>配送の記録。<c>(DestinationId, SiteId, ExternalKey)</c> で一意。</summary>
/// <param name="DestinationId">送り先の安定 ID（設定の <c>pleasanter.id</c>）。</param>
/// <param name="SiteId">送り先のサイト。</param>
/// <param name="SourceId">取り込み元の ID。</param>
/// <param name="ExternalKey">外部キー。</param>
/// <param name="RecordId">Pleasanter のレコード ID（分かれば）。</param>
/// <param name="MappingVersion">最後に送った時の対応づけ設定の版。</param>
/// <param name="ValueHash">最後に送った（または確認した）所有列の値ハッシュ。</param>
/// <param name="State">状態。</param>
/// <param name="AttemptCount">送信を試みた回数。</param>
/// <param name="LastOutcome">直近の結果の分類（理由の文章・本文は保存しない）。</param>
/// <param name="UpdatedAt">最終更新時刻。</param>
public sealed record Delivery(
    string DestinationId,
    long SiteId,
    string SourceId,
    string ExternalKey,
    long? RecordId,
    string? MappingVersion,
    string? ValueHash,
    DeliveryState State,
    int AttemptCount,
    string? LastOutcome,
    DateTimeOffset UpdatedAt);

/// <summary>配送の記録の更新。無ければ作り、あれば更新する。</summary>
/// <param name="CountAttempt">送信を試みたなら <c>true</c>（回数を 1 増やす）。</param>
public sealed record DeliverySave(
    string DestinationId,
    long SiteId,
    string SourceId,
    string ExternalKey,
    DeliveryState State,
    long? RecordId,
    string? MappingVersion,
    string? ValueHash,
    string? LastOutcome,
    bool CountAttempt);

/// <summary>取り込み実行の排他（同じ DB を使う全 CLI で 1 つ）。</summary>
public sealed record RunLease(Guid Id, string OwnerId, DateTimeOffset StartedAt);

/// <summary>取り込みの状態（取得状態・記事のスナップショット・配送・実行排他）の保存先。</summary>
public interface IIngestStateStore
{
    Task<SourceState?> GetSourceAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>記事と検証子を同じトランザクションで確定する。失敗したら何も変えない。</summary>
    Task CommitFetchAsync(FetchCommit commit, CancellationToken cancellationToken = default);

    /// <summary>検証子を捨てる（URL・認証の変更時、既存バッチなしの 304 のとき）。記事は残す。</summary>
    Task ClearValidatorsAsync(string sourceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredEntry>> ListEntriesAsync(string sourceId, CancellationToken cancellationToken = default);

    Task<Delivery?> GetDeliveryAsync(
        string destinationId, long siteId, string externalKey, CancellationToken cancellationToken = default);

    Task SaveDeliveryAsync(DeliverySave save, CancellationToken cancellationToken = default);

    /// <summary>送信中のまま残った配送を Unknown にする（前回の実行が中断された）。</summary>
    Task<int> MarkInterruptedAsUnknownAsync(string destinationId, CancellationToken cancellationToken = default);

    /// <summary>実行の排他を取る。他の実行が終わっていなければ <c>null</c>（引き継がない）。</summary>
    Task<RunLease?> TryBeginRunAsync(string ownerId, CancellationToken cancellationToken = default);

    Task EndRunAsync(Guid runId, string status, CancellationToken cancellationToken = default);

    Task<RunLease?> FindActiveRunAsync(CancellationToken cancellationToken = default);

    /// <summary>異常終了で残った排他を、運用者の明示操作で解除する。解除したら <c>true</c>。</summary>
    Task<bool> ForceEndActiveRunAsync(CancellationToken cancellationToken = default);
}
