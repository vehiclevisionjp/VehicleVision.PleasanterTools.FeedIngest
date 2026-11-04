using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter.Tests;

/// <summary>
/// DotNetClient を本ツールの組み方（<see cref="PleasanterGateway"/>）で使ったときの確認。
/// 分類など DotNetClient 自身の挙動は DotNetClient 側のテストが持つ。ここでは組み方と、0.2.0 に無い分の補いを確かめる。
/// </summary>
public sealed class PleasanterGatewayTests
{
    private const string SecretKey = "secret-key";

    private static PleasanterOptions OptionsFor(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        ApiKey = SecretKey,
        DetectServerVersion = false,
    };

    [Fact]
    public async Task リダイレクトを辿らず_本文_API_キーを別の接続先へ再送しない()
    {
        using var target = new LoopbackServer(_ => (HttpStatusCode.OK, "{}", null));
        using var origin = new LoopbackServer(_ => (HttpStatusCode.TemporaryRedirect, "", target.BaseUrl + "api/items/1/Create"));
        using var gateway = PleasanterGateway.Create(OptionsFor(origin.BaseUrl));

        var response = await gateway.CreateAsync(1, new Dictionary<string, object?> { ["Title"] = "t" });

        Assert.Equal(1, origin.RequestCount);
        Assert.Equal(0, target.RequestCount);
        Assert.Equal(307, response.StatusCode);
        Assert.Equal(PleasanterErrorKind.Permanent, response.ErrorKind);
    }

    [Theory]
    [InlineData("http://example.invalid")]
    [InlineData("https://user:pass@example.invalid")]
    [InlineData("https://example.invalid/?x=1")]
    [InlineData("https://example.invalid/#f")]
    public async Task BaseUrl_が不正なら送らずに_ArgumentException_を投げ_文言に秘密を出さない(string baseUrl)
    {
        var handler = new StubHandler();
        using var gateway = new PleasanterGateway(handler, OptionsFor(baseUrl));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => gateway.CheckConnectionAsync());

        Assert.Equal(0, handler.RequestCount);
        Assert.DoesNotContain(SecretKey, ex.Message);
        Assert.DoesNotContain("example.invalid", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_は_ApiKey_と_ApiVersion_をボディへ載せ_パスの形を変えない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"Id":42,"Message":"ok"}""");
        using var gateway = new PleasanterGateway(handler, OptionsFor("https://example.invalid/"));

        var response = await gateway.CreateAsync(100, new Dictionary<string, object?> { ["Title"] = "t" });

        Assert.True(response.IsSuccess);
        Assert.Equal(42, response.Id);
        Assert.Equal("https://example.invalid/api/items/100/Create", handler.RequestUri);
        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal(SecretKey, (string?)body["ApiKey"]);
        Assert.Equal(1.1m, (decimal?)body["ApiVersion"]);
        Assert.Equal("t", (string?)body["Title"]);
    }

    [Fact]
    public async Task 版の問い合わせなど追加の通信をしない()
    {
        var handler = new StubHandler();
        var options = new PleasanterOptions { BaseUrl = "https://version-probe.invalid", ApiKey = SecretKey };
        using var gateway = new PleasanterGateway(handler, options);

        await gateway.GetPageAsync(1, new Dictionary<string, object?>(), 0);

        Assert.Equal(1, handler.RequestCount);
        Assert.EndsWith("/api/items/1/Get", handler.RequestUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PleasanterErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.RequestTimeout, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, PleasanterErrorKind.Transient)]
    public async Task 失敗は例外にせず_ErrorKind_で分類する(HttpStatusCode status, PleasanterErrorKind expected)
    {
        using var gateway = new PleasanterGateway(new StubHandler(status), OptionsFor("https://example.invalid"));

        var response = await gateway.UpdateAsync(1, new Dictionary<string, object?>());

        Assert.Equal(expected, response.ErrorKind);
        Assert.False(response.IsSuccess);
    }

    [Theory]
    [InlineData(400, PleasanterErrorKind.Permanent)]
    [InlineData(401, PleasanterErrorKind.Unauthorized)]
    [InlineData(429, PleasanterErrorKind.Transient)]
    [InlineData(500, PleasanterErrorKind.Transient)]
    public async Task HTTP_200_でも本文の_StatusCode_が失敗なら失敗として分類する(int apiStatus, PleasanterErrorKind expected)
    {
        var handler = new StubHandler(HttpStatusCode.OK, $$"""{"StatusCode":{{apiStatus}},"Id":7}""");
        using var gateway = new PleasanterGateway(handler, OptionsFor("https://example.invalid"));

        var response = await gateway.CreateAsync(1, new Dictionary<string, object?>());

        Assert.Equal(expected, response.ErrorKind);
        Assert.False(response.IsSuccess);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(apiStatus, response.ApiStatusCode);
    }

    [Fact]
    public async Task 本文に_StatusCode_が無い_200_は成功のまま()
    {
        using var gateway = new PleasanterGateway(
            new StubHandler(HttpStatusCode.OK, """{"Id":"42"}"""), OptionsFor("https://example.invalid"));

        var response = await gateway.CreateAsync(1, new Dictionary<string, object?>());

        Assert.True(response.IsSuccess);
        Assert.Equal(42, response.Id);
    }

    [Fact]
    public async Task Retry_After_を秒と日時の両方で読む()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests)
        {
            RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)),
        };
        using var gateway = new PleasanterGateway(handler, OptionsFor("https://example.invalid"));

        var seconds = await gateway.GetPageAsync(1, new Dictionary<string, object?>(), 0);
        handler.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(5));
        var date = await gateway.GetPageAsync(1, new Dictionary<string, object?>(), 0);
        handler.RetryAfter = null;
        var none = await gateway.GetPageAsync(1, new Dictionary<string, object?>(), 0);

        Assert.Equal(TimeSpan.FromSeconds(30), seconds.RetryAfter);
        Assert.InRange(date.RetryAfter!.Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        Assert.Null(none.RetryAfter);
    }

    [Fact]
    public async Task 通信に失敗したら_Unknown_を返し_秘密を含めない()
    {
        using var gateway = new PleasanterGateway(
            new StubHandler(throwOnSend: true), OptionsFor("https://example.invalid"));

        var response = await gateway.CreateAsync(1, new Dictionary<string, object?>());

        Assert.Equal(PleasanterErrorKind.Unknown, response.ErrorKind);
        Assert.Null(response.StatusCode);
        Assert.Null(response.RetryAfter);
        Assert.DoesNotContain(SecretKey, response.ToString());
    }

    [Fact]
    public async Task 呼び出し側の取り消しは_Unknown_にせず投げる()
    {
        using var gateway = new PleasanterGateway(new StubHandler(), OptionsFor("https://example.invalid"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.CreateAsync(1, new Dictionary<string, object?>(), cts.Token));
    }

    private sealed class StubHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}",
        bool throwOnSend = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        public RetryConditionHeaderValue? RetryAfter { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (throwOnSend)
            {
                throw new HttpRequestException("stub failure");
            }

            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            response.Headers.RetryAfter = RetryAfter;
            return response;
        }
    }

    /// <summary>ループバックで待ち受け、受けた回数を数える（リダイレクトを辿るかの確認用）。</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<HttpListenerRequest, (HttpStatusCode Status, string Body, string? Location)> _respond;
        private int _requestCount;

        public LoopbackServer(Func<HttpListenerRequest, (HttpStatusCode, string, string?)> respond)
        {
            _respond = respond;
            BaseUrl = $"http://127.0.0.1:{FreePort()}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public void Dispose() => _listener.Close();

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    await reader.ReadToEndAsync();
                }

                Interlocked.Increment(ref _requestCount);
                var (status, body, location) = _respond(context.Request);
                context.Response.StatusCode = (int)status;
                if (location is not null)
                {
                    context.Response.RedirectLocation = location;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
    }
}
