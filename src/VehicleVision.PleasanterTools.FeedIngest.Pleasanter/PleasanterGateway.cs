using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

/// <summary>
/// DotNetClient の <see cref="PleasanterApiClient"/> を、本ツールの呼び方で包む。
/// </summary>
/// <remarks>
/// <para>通信は <see cref="PleasanterHttp.CreateHandler"/>（リダイレクトを辿らない・Cookie 無し）の <see cref="HttpClient"/> で行う。
/// API キーは本文に載るため、<c>BaseUrl</c> の検査と秘密を出さない扱いは DotNetClient に任せる。</para>
/// <para>HTTP が 200 でも本文の <c>StatusCode</c> が 200 以外なら失敗として分類する処理と、Retry-After の読み取り
/// （<see cref="PleasanterResponse.RetryAfter"/>）は DotNetClient 0.4.0 の機能を使う。待機が長いときに次回へ回す判断は呼び出し側が行う。</para>
/// <para>型付きのメソッド（<c>client.Items.*</c>）は版の問い合わせを 1 回行うが、<see cref="PleasanterApiClient.PostJsonAsync"/> は行わない。
/// 本ツールは API 呼び出しの回数を数えるため、後者だけを使う。</para>
/// </remarks>
public sealed class PleasanterGateway : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly PleasanterApiClient _client;

    /// <summary>通信の経路（ハンドラー）を渡して作る。ハンドラーは、このクラスを破棄するときに一緒に破棄する。</summary>
    public PleasanterGateway(HttpMessageHandler handler, IPleasanterOptionsProvider optionsProvider)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(optionsProvider);
        _httpClient = new HttpClient(handler, disposeHandler: true);
        _client = new PleasanterApiClient(_httpClient, optionsProvider);
    }

    /// <summary>通信の経路（ハンドラー）と固定の接続設定で作る。</summary>
    public PleasanterGateway(HttpMessageHandler handler, PleasanterOptions options)
        : this(handler, new StaticPleasanterOptionsProvider(options))
    {
    }

    /// <summary>本番用。リダイレクトを辿らず、Cookie を持たない。</summary>
    public static PleasanterGateway Create(IPleasanterOptionsProvider optionsProvider)
        => new(PleasanterHttp.CreateHandler(), optionsProvider);

    /// <summary>本番用。リダイレクトを辿らず、Cookie を持たない。</summary>
    public static PleasanterGateway Create(PleasanterOptions options)
        => new(PleasanterHttp.CreateHandler(), options);

    /// <summary>接続先へ到達し、API キーが拒否されないことを確かめる。</summary>
    public Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
        => _client.CheckConnectionAsync(cancellationToken);

    /// <summary>レコードを作る。</summary>
    public Task<PleasanterResponse> CreateAsync(
        long siteId, IReadOnlyDictionary<string, object?> record, CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/Create", record, cancellationToken);

    /// <summary>レコードを更新する。</summary>
    public Task<PleasanterResponse> UpdateAsync(
        long referenceId, IReadOnlyDictionary<string, object?> record, CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{referenceId}/Update", record, cancellationToken);

    /// <summary>サイトのレコードを、先頭からの位置を指定して取り出す（全ページの走査用）。絞り込みは <c>View</c> に渡す。</summary>
    public Task<PleasanterResponse> GetPageAsync(
        long siteId, IReadOnlyDictionary<string, object?> view, int offset, CancellationToken cancellationToken = default)
        => PostAsync(
            $"api/items/{siteId}/Get",
            new Dictionary<string, object?> { ["View"] = view, ["Offset"] = offset },
            cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    private Task<PleasanterResponse> PostAsync(
        string path, IReadOnlyDictionary<string, object?> body, CancellationToken cancellationToken)
    {
        var node = JsonSerializer.SerializeToNode(body, SerializerOptions) as JsonObject ?? [];
        return _client.PostJsonAsync(path, node, cancellationToken);
    }
}
