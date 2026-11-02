namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>取得の失敗の分類。メッセージに URL・ヘッダ値・本文を含めない。</summary>
public enum FeedFetchErrorKind
{
    /// <summary>通信断・名前解決失敗・TLS 失敗など。再試行を使い切った。</summary>
    Transport,

    /// <summary>1 要求または取得全体の時間切れ。</summary>
    Timeout,

    /// <summary>429、または待機指定が長すぎる。次回へ回す。</summary>
    RateLimit,

    /// <summary>401・403。取り込み元を停止し、人が直す。</summary>
    Authentication,

    /// <summary>その他の非成功ステータス（404 等の 4xx、再試行を使い切った 5xx）。</summary>
    HttpStatus,

    /// <summary>リダイレクトの上限超過・降格・別 origin・不正な Location。</summary>
    Redirect,

    /// <summary>取得先が内部・予約アドレス、または HTTPS でない。</summary>
    Destination,

    /// <summary>圧縮前後の容量の上限超過。</summary>
    LimitExceeded,

    /// <summary>未対応の Content-Encoding。</summary>
    UnsupportedEncoding,
}

/// <summary>取得の失敗。</summary>
public sealed class FeedFetchException(FeedFetchErrorKind kind, int? statusCode, string message) : Exception(message)
{
    public FeedFetchErrorKind Kind { get; } = kind;

    /// <summary>HTTP ステータス。応答が無ければ <c>null</c>。</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>条件付き取得の検証子。ETag は引用符・弱いタグも含めたまま保持する。</summary>
public sealed record FeedValidators(string? ETag, DateTimeOffset? LastModified);

/// <summary>取得の結果。</summary>
/// <param name="Body">本文（圧縮は解除済み）。304 のときは <c>null</c>。</param>
/// <param name="NotModified">304（取得表現に変更なし）。</param>
/// <param name="FinalUrl">リダイレクト後の最終 URL（相対 URI の解決の基準）。</param>
/// <param name="Charset">Content-Type の charset。無ければ <c>null</c>。</param>
/// <param name="Validators">応答の検証子。200 で ETag／Last-Modified が無ければ両方 <c>null</c>（古い値を引き継がない）。</param>
public sealed record FeedFetchResult(
    byte[]? Body, bool NotModified, Uri FinalUrl, string? Charset, FeedValidators Validators);

/// <summary>取得の設定。既定値はアーキテクチャ方針 3 章（U9）。</summary>
public sealed record FeedFetchOptions
{
    /// <summary>1 要求（本文読取を含む）の時間。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>1 取り込み元の取得全体（再試行・待機・リダイレクトを含む）の時間。</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public int MaxRedirects { get; init; } = 5;

    /// <summary>圧縮前・圧縮後のそれぞれの上限。</summary>
    public int MaxBodyBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>初回に加える再試行の最大回数。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Retry-After の待機をこれより長く求められたら、再試行せず RateLimit にする。</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);
}
