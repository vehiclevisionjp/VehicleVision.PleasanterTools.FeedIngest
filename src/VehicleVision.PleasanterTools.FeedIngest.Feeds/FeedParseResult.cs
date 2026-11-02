using VehicleVision.PleasanterTools.FeedIngest.Core;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>フィード全体を処理できない理由。バッチを部分確定しない。</summary>
public enum FeedParseErrorKind
{
    /// <summary>文字コードの判定・復号に失敗（矛盾・未知・不正バイト）。</summary>
    Encoding,

    /// <summary>XML として不正、または DTD を含む。</summary>
    Xml,

    /// <summary>RSS 2.0 / Atom 1.0 以外、または設定した形式と異なる。</summary>
    UnsupportedFormat,

    /// <summary>容量・記事数の上限超過。</summary>
    LimitExceeded,
}

/// <summary>フィード全体のエラー。メッセージに本文・URL・元 ID を含めない。</summary>
public sealed class FeedParseException(FeedParseErrorKind kind, string message) : Exception(message)
{
    public FeedParseErrorKind Kind { get; } = kind;
}

/// <summary>1 項目を正規化できなかった理由。他の項目は処理を続ける。</summary>
/// <param name="Index">フィード内の項目の位置（0 始まり）。</param>
/// <param name="Reason">理由（本文・元 ID を含めない）。</param>
public sealed record FeedItemError(int Index, string Reason);

/// <summary>同一フィード内で、同じ ID なのに内容が異なる項目。送信せず保留する。</summary>
/// <param name="IdentityKind">識別子の種別。</param>
/// <param name="Identity">元 ID または link の文字列（呼び出し側が保留の扱いに使う。ログへ出さない）。</param>
/// <param name="Count">競合した項目数（同一内容は 1 件にまとめた後の数）。</param>
public sealed record FeedItemConflict(EntryIdentityKind IdentityKind, string Identity, int Count);

/// <summary>解析の結果。</summary>
/// <param name="Format">判定したフィード形式（<see cref="FeedFormat.Rss20"/> か <see cref="FeedFormat.Atom10"/>）。</param>
/// <param name="Entries">正規化できた記事（フィード内の出現順、同一 ID・同一内容は 1 件）。</param>
/// <param name="ItemErrors">正規化できなかった項目。</param>
/// <param name="Conflicts">同一 ID・異内容の競合（該当する記事は <paramref name="Entries"/> に含めない）。</param>
public sealed record FeedParseResult(
    FeedFormat Format,
    IReadOnlyList<NormalizedEntry> Entries,
    IReadOnlyList<FeedItemError> ItemErrors,
    IReadOnlyList<FeedItemConflict> Conflicts);

/// <summary>解析の上限。既定値はアーキテクチャ方針 3 章（U9）。</summary>
public sealed record FeedParserLimits(
    int MaxBytes = 10 * 1024 * 1024,
    int MaxItems = 10_000,
    int MaxItemContentBytes = 1024 * 1024);
