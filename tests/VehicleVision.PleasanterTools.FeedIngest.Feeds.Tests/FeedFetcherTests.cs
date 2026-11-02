using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds.Tests;

public sealed class FeedFetcherTests
{
    private static readonly Uri Url = new("https://feeds.example.test/news.xml");
    private static readonly AuthenticationConfiguration NoAuth = new(AuthenticationKind.None, null, null, []);

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }

    private static HttpResponseMessage Ok(byte[]? body = null, string contentType = "application/rss+xml; charset=utf-8")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body ?? "<rss/>"u8.ToArray()) };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    private static Task<IPAddress[]> PublicResolver(string host, CancellationToken token)
        => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private static FeedFetcher Create(
        HttpMessageHandler handler, FeedFetchOptions? options = null, List<TimeSpan>? delays = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
        => new(handler, options, resolver ?? PublicResolver, (span, _) =>
        {
            delays?.Add(span);
            return Task.CompletedTask;
        });

    private static async Task<FeedFetchException> FailAsync(FeedFetcher fetcher, Uri? url = null, FeedValidators? validators = null)
        => await Assert.ThrowsAsync<FeedFetchException>(() => fetcher.FetchAsync(url ?? Url, NoAuth, validators));

    [Fact]
    public async Task 取得した本文と文字コードと検証子を返す()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = Ok("<rss>日本語</rss>"u8.ToArray());
            response.Headers.ETag = new EntityTagHeaderValue("\"abc\"", isWeak: true);
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
            return response;
        });
        using var fetcher = Create(handler);

        var result = await fetcher.FetchAsync(Url, NoAuth);

        Assert.Equal("<rss>日本語</rss>", Encoding.UTF8.GetString(result.Body!));
        Assert.False(result.NotModified);
        Assert.Equal("utf-8", result.Charset);
        Assert.Equal("W/\"abc\"", result.Validators.ETag);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), result.Validators.LastModified);
        Assert.Equal(Url, result.FinalUrl);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task 検証子を条件付きヘッダで送り304はNotModifiedを返す()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotModified));
        using var fetcher = Create(handler);
        var validators = new FeedValidators("W/\"abc\"", new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero));

        var result = await fetcher.FetchAsync(Url, NoAuth, validators);

        Assert.True(result.NotModified);
        Assert.Null(result.Body);
        Assert.Equal(validators, result.Validators);
        var request = handler.Requests[0];
        Assert.Equal("W/\"abc\"", request.Headers.GetValues("If-None-Match").Single());
        Assert.Equal("Thu, 22 Oct 2026 00:00:00 GMT", request.Headers.GetValues("If-Modified-Since").Single());
    }

    [Fact]
    public async Task 検証子を送っていない304はエラー()
    {
        using var fetcher = Create(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotModified)));

        Assert.Equal(FeedFetchErrorKind.HttpStatus, (await FailAsync(fetcher)).Kind);
    }

    [Fact]
    public async Task 応答200でETagもLastModifiedも無ければ古い値を引き継がない()
    {
        using var fetcher = Create(new StubHandler((_, _) => Ok()));

        var result = await fetcher.FetchAsync(Url, NoAuth, new FeedValidators("\"old\"", DateTimeOffset.UnixEpoch));

        Assert.Equal(new FeedValidators(null, null), result.Validators);
    }

    // 認証

    [Fact]
    public async Task Basic認証とヘッダ認証を送る()
    {
        var env = new Dictionary<string, string> { ["U"] = "user", ["P"] = "pä:ss", ["T"] = "Bearer tok" };
        var handler = new StubHandler((_, _) => Ok());
        using var fetcher = Create(handler);

        await fetcher.FetchAsync(
            Url,
            new AuthenticationConfiguration(AuthenticationKind.Basic, new SecretReference("U"), new SecretReference("P"), []),
            environment: name => env.GetValueOrDefault(name));
        await fetcher.FetchAsync(
            Url,
            new AuthenticationConfiguration(
                AuthenticationKind.Headers, null, null,
                [new HeaderConfiguration("Authorization", new SecretReference("T")), new HeaderConfiguration("X-Feed-Token", new SecretReference("T"))]),
            environment: name => env.GetValueOrDefault(name));

        var expectedBasic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pä:ss"));
        Assert.Equal(expectedBasic, handler.Requests[0].Headers.GetValues("Authorization").Single());
        Assert.Equal("Bearer tok", handler.Requests[1].Headers.GetValues("Authorization").Single());
        Assert.Equal("Bearer tok", handler.Requests[1].Headers.GetValues("X-Feed-Token").Single());
    }

    [Fact]
    public async Task 秘密が解決できなければ秘密の値を含まない設定エラー()
    {
        using var fetcher = Create(new StubHandler((_, _) => Ok()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.FetchAsync(
            Url,
            new AuthenticationConfiguration(AuthenticationKind.Basic, new SecretReference("U"), new SecretReference("P"), []),
            environment: _ => null));

        Assert.Contains("U", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 認証は同じoriginのリダイレクトだけへ引き継ぎ別originは拒否して送らない()
    {
        var env = new Dictionary<string, string> { ["T"] = "secret-token" };
        var auth = new AuthenticationConfiguration(
            AuthenticationKind.Headers, null, null, [new HeaderConfiguration("X-Feed-Token", new SecretReference("T"))]);

        var sameOrigin = new StubHandler((request, count) => count == 1
            ? Redirect("/moved.xml")
            : Ok());
        using (var fetcher = Create(sameOrigin))
        {
            await fetcher.FetchAsync(Url, auth, environment: name => env.GetValueOrDefault(name));
        }

        Assert.Equal("https://feeds.example.test/moved.xml", sameOrigin.Requests[1].RequestUri!.AbsoluteUri);
        Assert.True(sameOrigin.Requests[1].Headers.Contains("X-Feed-Token"));

        var crossOrigin = new StubHandler((_, _) => Redirect("https://evil.example.test/x"));
        using var other = Create(crossOrigin);
        var exception = await Assert.ThrowsAsync<FeedFetchException>(
            () => other.FetchAsync(Url, auth, environment: name => env.GetValueOrDefault(name)));
        Assert.Equal(FeedFetchErrorKind.Redirect, exception.Kind);
        Assert.Single(crossOrigin.Requests);
        Assert.DoesNotContain("secret-token", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    // リダイレクト

    [Fact]
    public async Task リダイレクトは5回まで追従し6回目で拒否する()
    {
        var handler = new StubHandler((_, count) => count <= 5 ? Redirect($"/r{count}.xml") : Ok());
        using (var fetcher = Create(handler))
        {
            var result = await fetcher.FetchAsync(Url, NoAuth);
            Assert.Equal("https://feeds.example.test/r5.xml", result.FinalUrl.AbsoluteUri);
        }

        var endless = new StubHandler((_, count) => Redirect($"/r{count}.xml"));
        using var limited = Create(endless);
        Assert.Equal(FeedFetchErrorKind.Redirect, (await FailAsync(limited)).Kind);
        Assert.Equal(6, endless.Requests.Count);
    }

    [Theory]
    [InlineData("http://feeds.example.test/news.xml")]
    [InlineData("https://user:pass@feeds.example.test/news.xml")]
    [InlineData("https://feeds.example.test:8443/news.xml")]
    public async Task HTTPへの降格と資格情報と別portのリダイレクトを拒否する(string location)
    {
        var handler = new StubHandler((_, _) => Redirect(location));
        using var fetcher = Create(handler);

        Assert.Equal(FeedFetchErrorKind.Redirect, (await FailAsync(fetcher)).Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Locationが無いリダイレクトはエラー()
    {
        using var fetcher = Create(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Found)));

        Assert.Equal(FeedFetchErrorKind.Redirect, (await FailAsync(fetcher)).Kind);
    }

    // 宛先

    [Theory]
    [InlineData("http://feeds.example.test/news.xml")]
    [InlineData("https://user@feeds.example.test/news.xml")]
    [InlineData("https://feeds.example.test/news.xml#frag")]
    public async Task HTTPSでないURLやuserinfoやfragmentは送信前に拒否する(string url)
    {
        var handler = new StubHandler((_, _) => Ok());
        using var fetcher = Create(handler);

        Assert.Equal(FeedFetchErrorKind.Destination, (await FailAsync(fetcher, new Uri(url))).Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:93.184.216.34", true)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    public void 内部と予約のアドレスを拒否する(string address, bool allowed)
    {
        Assert.Equal(allowed, DestinationGuard.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("https://127.0.0.1/feed")]
    [InlineData("https://[::1]/feed")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public async Task IPリテラルの内部宛を送信前に拒否する(string url)
    {
        var handler = new StubHandler((_, _) => Ok());
        using var fetcher = Create(handler);

        Assert.Equal(FeedFetchErrorKind.Destination, (await FailAsync(fetcher, new Uri(url))).Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 名前が内部アドレスに解決されるなら拒否し一部でも内部なら拒否する()
    {
        var handler = new StubHandler((_, _) => Ok());
        using var internalOnly = Create(handler, resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }));
        using var mixed = Create(handler, resolver: (_, _) => Task.FromResult(
            new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("::1") }));

        Assert.Equal(FeedFetchErrorKind.Destination, (await FailAsync(internalOnly)).Kind);
        Assert.Equal(FeedFetchErrorKind.Destination, (await FailAsync(mixed)).Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task リダイレクト先でも宛先を再検査する()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) => Redirect("/moved.xml"));
        using var fetcher = Create(handler, resolver: (_, _) => Task.FromResult(
            new[] { IPAddress.Parse(++calls == 1 ? "93.184.216.34" : "192.168.0.1") }));

        Assert.Equal(FeedFetchErrorKind.Destination, (await FailAsync(fetcher)).Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 名前解決に失敗したら通信エラー()
    {
        using var fetcher = Create(
            new StubHandler((_, _) => Ok()),
            resolver: (_, _) => throw new System.Net.Sockets.SocketException());

        Assert.Equal(FeedFetchErrorKind.Transport, (await FailAsync(fetcher)).Kind);
    }

    // 容量・圧縮

    [Fact]
    public async Task 本文は上限ちょうどを通し1バイト超過を拒否する()
    {
        var body = new byte[100];
        using var exact = Create(new StubHandler((_, _) => Ok(body)), new FeedFetchOptions { MaxBodyBytes = 100 });
        using var over = Create(new StubHandler((_, _) => Ok(body)), new FeedFetchOptions { MaxBodyBytes = 99 });

        Assert.Equal(100, (await exact.FetchAsync(Url, NoAuth)).Body!.Length);
        Assert.Equal(FeedFetchErrorKind.LimitExceeded, (await FailAsync(over)).Kind);
    }

    [Fact]
    public async Task ContentLengthを偽っても実際に読んだ量で上限を守る()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[500])) };
            return response;
        });
        using var fetcher = Create(handler, new FeedFetchOptions { MaxBodyBytes = 100 });

        Assert.Equal(FeedFetchErrorKind.LimitExceeded, (await FailAsync(fetcher)).Kind);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static HttpResponseMessage GzipResponse(byte[] data)
    {
        var response = Ok(Gzip(data));
        response.Content.Headers.ContentEncoding.Add("gzip");
        return response;
    }

    [Fact]
    public async Task gzipを展開し展開後の上限を守る()
    {
        var plain = Encoding.UTF8.GetBytes(new string('a', 1000));
        using var ok = Create(new StubHandler((_, _) => GzipResponse(plain)), new FeedFetchOptions { MaxBodyBytes = 1000 });
        using var bomb = Create(new StubHandler((_, _) => GzipResponse(plain)), new FeedFetchOptions { MaxBodyBytes = 999 });

        Assert.Equal(plain, (await ok.FetchAsync(Url, NoAuth)).Body);
        Assert.Equal(FeedFetchErrorKind.LimitExceeded, (await FailAsync(bomb)).Kind);
    }

    [Fact]
    public async Task 圧縮したままの量にも上限を適用する()
    {
        var random = new byte[200];
        new Random(1).NextBytes(random);
        var compressed = Gzip(random);
        using var fetcher = Create(
            new StubHandler((_, _) => GzipResponse(random)),
            new FeedFetchOptions { MaxBodyBytes = compressed.Length - 1 });

        Assert.Equal(FeedFetchErrorKind.LimitExceeded, (await FailAsync(fetcher)).Kind);
    }

    [Fact]
    public async Task 未対応のContentEncodingと壊れたgzipはエラー()
    {
        var br = new StubHandler((_, _) =>
        {
            var response = Ok();
            response.Content.Headers.ContentEncoding.Add("br");
            return response;
        });
        var broken = new StubHandler((_, _) =>
        {
            var response = Ok("not gzip data"u8.ToArray());
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        });
        using var brFetcher = Create(br);
        using var brokenFetcher = Create(broken);

        Assert.Equal(FeedFetchErrorKind.UnsupportedEncoding, (await FailAsync(brFetcher)).Kind);
        Assert.Equal(FeedFetchErrorKind.UnsupportedEncoding, (await FailAsync(brokenFetcher)).Kind);
    }

    [Fact]
    public async Task Acceptエンコーディングはgzipだけを要求する()
    {
        var handler = new StubHandler((_, _) => Ok());
        using var fetcher = Create(handler);

        await fetcher.FetchAsync(Url, NoAuth);

        Assert.Equal("gzip", handler.Requests[0].Headers.GetValues("Accept-Encoding").Single());
    }

    // 時間

    [Fact]
    public async Task 単一要求の時間切れは再試行し使い切ればTimeout()
    {
        var calls = 0;
        var handler = new AsyncStubHandler(async (_, token) =>
        {
            calls++;
            await Task.Delay(Timeout.Infinite, token);
            return Ok();
        });
        using var fetcher = Create(
            handler, new FeedFetchOptions { RequestTimeout = TimeSpan.FromMilliseconds(30) }, delays: []);

        Assert.Equal(FeedFetchErrorKind.Timeout, (await FailAsync(fetcher)).Kind);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task 取得全体の時間切れでTimeoutにする()
    {
        var handler = new AsyncStubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Ok();
        });
        using var fetcher = Create(handler, new FeedFetchOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(10),
            TotalTimeout = TimeSpan.FromMilliseconds(50),
        });

        Assert.Equal(FeedFetchErrorKind.Timeout, (await FailAsync(fetcher)).Kind);
    }

    [Fact]
    public async Task 呼び出し側の中断はキャンセルのまま伝える()
    {
        var handler = new AsyncStubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Ok();
        });
        using var fetcher = Create(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.FetchAsync(Url, NoAuth, cancellationToken: cts.Token));
    }

    // 再試行

    [Fact]
    public async Task 一時的な失敗は1_2_4秒待って最大3回再試行する()
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler((_, count) => count < 4 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Ok());
        using var fetcher = Create(handler, delays: delays);

        await fetcher.FetchAsync(Url, NoAuth);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([1, 2, 4], delays.Select(d => (int)d.TotalSeconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task 再試行を使い切った5xxと408はHttpStatus(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(status));
        using var fetcher = Create(handler, delays: []);

        var exception = await FailAsync(fetcher);

        Assert.Equal(FeedFetchErrorKind.HttpStatus, exception.Kind);
        Assert.Equal((int)status, exception.StatusCode);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task 通信断は再試行して使い切ればTransport()
    {
        var handler = new AsyncStubHandler((_, _) => throw new HttpRequestException("boom https://secret.example.test/?k=1"));
        using var fetcher = Create(handler, delays: []);

        var exception = await FailAsync(fetcher);

        Assert.Equal(FeedFetchErrorKind.Transport, exception.Kind);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task 再試行しないステータス(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(status));
        using var fetcher = Create(handler);

        Assert.Equal(FeedFetchErrorKind.HttpStatus, (await FailAsync(fetcher)).Kind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 認証エラーは再試行しない(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(status));
        using var fetcher = Create(handler);

        var exception = await FailAsync(fetcher);

        Assert.Equal(FeedFetchErrorKind.Authentication, exception.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 応答429のRetryAfter秒を待ってから再試行する()
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler((_, count) =>
        {
            if (count > 1)
            {
                return Ok();
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });
        using var fetcher = Create(handler, delays: delays);

        await fetcher.FetchAsync(Url, NoAuth);

        Assert.Equal(7, Assert.Single(delays).TotalSeconds);
    }

    [Fact]
    public async Task 応答429のRetryAfterが60秒を超えれば再試行せずRateLimit()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(61));
            return response;
        });
        using var fetcher = Create(handler, delays: []);

        var exception = await FailAsync(fetcher);

        Assert.Equal(FeedFetchErrorKind.RateLimit, exception.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 応答429のRetryAfterの日時形式も解釈し既定の待機を下回らない()
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler((_, count) =>
        {
            if (count > 1)
            {
                return Ok();
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5));
            return response;
        });
        using var fetcher = Create(handler, delays: delays);

        await fetcher.FetchAsync(Url, NoAuth);

        Assert.Equal(1, Assert.Single(delays).TotalSeconds);
    }

    [Fact]
    public async Task 使い切った応答429はRateLimit()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var fetcher = Create(handler, delays: []);

        var exception = await FailAsync(fetcher);

        Assert.Equal(FeedFetchErrorKind.RateLimit, exception.Kind);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public void 本番用ハンドラーは自動リダイレクトとCookieとプロキシと自動展開を使わない()
    {
        using var handler = FeedFetcher.CreateDefaultHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.NotNull(handler.ConnectCallback);
    }
}
