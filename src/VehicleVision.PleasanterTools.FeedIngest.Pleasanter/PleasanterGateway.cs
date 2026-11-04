using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter;

/// <summary>
/// <see cref="PleasanterResponse"/> に、DotNetClient 0.2.0 が返さない 2 つ（本文の <c>StatusCode</c>・Retry-After）を足した結果。
/// </summary>
/// <param name="ErrorKind">扱い方。HTTP が 200 でも <paramref name="ApiStatusCode"/> が 200 以外なら、その値で分類し直した結果。</param>
/// <param name="StatusCode">HTTP のステータス。応答が無ければ <c>null</c>。</param>
/// <param name="Id">作成・更新されたレコードの ID。</param>
/// <param name="Message">Pleasanter が返した文言。</param>
/// <param name="Body">応答の中身。</param>
/// <param name="ApiStatusCode">応答 JSON の <c>StatusCode</c>。</param>
/// <param name="RetryAfter">Retry-After ヘッダ（秒・日時のどちらも）。無ければ <c>null</c>。</param>
public sealed record PleasanterResult(
    PleasanterErrorKind ErrorKind,
    int? StatusCode = null,
    long? Id = null,
    string? Message = null,
    JsonNode? Body = null,
    int? ApiStatusCode = null,
    TimeSpan? RetryAfter = null)
    : PleasanterResponse(ErrorKind, StatusCode, Id, Message, Body);

/// <summary>
/// DotNetClient の <see cref="PleasanterApiClient"/> を、本ツールの呼び方で包む。
/// </summary>
/// <remarks>
/// <para>通信は <see cref="PleasanterHttp.CreateHandler"/>（リダイレクトを辿らない・Cookie 無し）の <see cref="HttpClient"/> で行う。
/// API キーは本文に載るため、<c>BaseUrl</c> の検査と秘密を出さない扱いは DotNetClient に任せる。</para>
/// <para>**DotNetClient 0.2.0 に無いものを、ここで補っている。** 該当の変更を含む版へ上げたら、不要になった分を消す。</para>
/// <list type="bullet">
/// <item><description>HTTP が 200 でも、本文の <c>StatusCode</c> が 200 以外なら失敗として分類する
/// （DotNetClient #95・PR #96。develop にあるが 0.2.0 には無い）</description></item>
/// <item><description>Retry-After ヘッダを読む（DotNetClient は返さない）。待機指定が長いときに次回へ回すために使う</description></item>
/// </list>
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
        _httpClient = new HttpClient(new RetryAfterRecorder(handler), disposeHandler: true);
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
    public Task<PleasanterResult> CreateAsync(
        long siteId, IReadOnlyDictionary<string, object?> record, CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/Create", record, cancellationToken);

    /// <summary>レコードを更新する。</summary>
    public Task<PleasanterResult> UpdateAsync(
        long referenceId, IReadOnlyDictionary<string, object?> record, CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{referenceId}/Update", record, cancellationToken);

    /// <summary>サイトのレコードを、先頭からの位置を指定して取り出す（全ページの走査用）。絞り込みは <c>View</c> に渡す。</summary>
    public Task<PleasanterResult> GetPageAsync(
        long siteId, IReadOnlyDictionary<string, object?> view, int offset, CancellationToken cancellationToken = default)
        => PostAsync(
            $"api/items/{siteId}/Get",
            new Dictionary<string, object?> { ["View"] = view, ["Offset"] = offset },
            cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    internal static PleasanterErrorKind Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK => PleasanterErrorKind.None,
        HttpStatusCode.Unauthorized => PleasanterErrorKind.Unauthorized,
        HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            => PleasanterErrorKind.Permanent,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            => PleasanterErrorKind.Transient,
        _ => (int)statusCode >= 500 ? PleasanterErrorKind.Transient : PleasanterErrorKind.Permanent,
    };

    private async Task<PleasanterResult> PostAsync(
        string path, IReadOnlyDictionary<string, object?> body, CancellationToken cancellationToken)
    {
        var node = JsonSerializer.SerializeToNode(body, SerializerOptions) as JsonObject ?? [];
        var slot = new RetryAfterSlot();
        RetryAfterRecorder.Current.Value = slot;
        PleasanterResponse response;
        try
        {
            response = await _client.PostJsonAsync(path, node, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RetryAfterRecorder.Current.Value = null;
        }

        var apiStatus = ReadInt(response.Body?["StatusCode"]);
        var kind = response.ErrorKind;
        if (kind is PleasanterErrorKind.None && apiStatus is not null && apiStatus != 200)
        {
            // HTTP は 200 でも、JSON の StatusCode が失敗なら失敗として扱う
            kind = Classify((HttpStatusCode)apiStatus.Value);
        }

        return new PleasanterResult(
            kind,
            response.StatusCode,
            response.Id ?? ReadLong(response.Body?["Id"]),
            response.Message,
            response.Body,
            apiStatus,
            // 応答が無い（Unknown）ときは、書き留めた値が使えない
            response.StatusCode is null ? null : slot.Value);
    }

    private static int? ReadInt(JsonNode? node)
        => ReadLong(node) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private sealed class RetryAfterSlot
    {
        public TimeSpan? Value { get; set; }
    }

    /// <summary>応答の Retry-After を、呼び出しごとの入れ物へ書き留める（DotNetClient が返さないため）。</summary>
    private sealed class RetryAfterRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        // 呼び出しの非同期の流れにだけ付く。同時の呼び出しは互いの記録を見ない
        public static readonly AsyncLocal<RetryAfterSlot?> Current = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (Current.Value is { } slot)
            {
                // 同じ呼び出しの中に別の要求があっても、最後の応答の値が残る
                slot.Value = Parse(response);
            }

            return response;
        }

        private static TimeSpan? Parse(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header?.Delta is { } delta)
            {
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }

            if (header?.Date is { } date)
            {
                var span = date - DateTimeOffset.UtcNow;
                return span < TimeSpan.Zero ? TimeSpan.Zero : span;
            }

            return null;
        }
    }
}
