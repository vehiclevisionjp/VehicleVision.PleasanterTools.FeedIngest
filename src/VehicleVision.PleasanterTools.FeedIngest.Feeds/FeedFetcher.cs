using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds;

/// <summary>
/// 制限付きでフィードを取得する（HTTPS のみ・手動リダイレクト・宛先検査・容量と時間の上限・条件付き取得）。
/// </summary>
/// <remarks>
/// <para>解析ライブラリへ URL を渡さず、ここで取得したバイト列を <see cref="FeedParser"/> へ渡す。</para>
/// <para>認証の資格情報は元の取得先だけへ送る。リダイレクトは同じ origin（scheme・host・port）だけ追従し、
/// 別 origin・HTTP への降格は拒否する。例外のメッセージに URL・ヘッダ値・本文を含めない。</para>
/// <para>検証子（ETag／Last-Modified）の永続化はここでは行わない。呼び出し側が保存・判断する。</para>
/// </remarks>
public sealed class FeedFetcher : IDisposable
{
    private static readonly int[] BackoffSeconds = [1, 2, 4];

    private readonly HttpClient _client;
    private readonly FeedFetchOptions _options;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolver;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly DestinationPolicy _policy;
    private readonly HttpMessageHandler? _ownedHandler;

    /// <param name="handler">通信を行うハンドラー。試験では差し替える。本番は <see cref="CreateDefaultHandler"/>。</param>
    /// <param name="options">上限。</param>
    /// <param name="resolver">事前の宛先検査に使う名前解決。省略時は DNS。</param>
    /// <param name="delay">再試行の待機。省略時は実際に待つ。試験は実時間を待たない。</param>
    /// <param name="policy">この取得だけに効く宛先の方針。省略時は許可リストなし（内部・予約宛を常に拒否）。</param>
    public FeedFetcher(
        HttpMessageHandler handler,
        FeedFetchOptions? options = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        DestinationPolicy? policy = null)
        : this(handler, null, options, resolver, delay, policy)
    {
    }

    private FeedFetcher(
        HttpMessageHandler handler,
        HttpMessageHandler? ownedHandler,
        FeedFetchOptions? options,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver,
        Func<TimeSpan, CancellationToken, Task>? delay,
        DestinationPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _options = options ?? new FeedFetchOptions();
        _client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _resolver = resolver ?? DefaultResolver;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _policy = policy ?? DestinationPolicy.Default;
        _ownedHandler = ownedHandler;
    }

    /// <summary>
    /// 本番用。<paramref name="policy"/>（source の許可リスト）を接続時の検査へ結び付けた専用のハンドラーを作り、
    /// このインスタンスが所有する。接続のプールを source 間で共有しないので、許可が他の source へ漏れない。
    /// </summary>
    public static FeedFetcher CreateDefault(FeedFetchOptions options, DestinationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var handler = CreateDefaultHandler(policy);
        return new FeedFetcher(handler, handler, options, null, null, policy);
    }

    public void Dispose()
    {
        _client.Dispose();
        _ownedHandler?.Dispose();
    }

    /// <summary>
    /// 本番用のハンドラー。自動リダイレクト・Cookie・プロキシ・自動展開を使わず、接続時に宛先を検査して
    /// 検査したアドレスへだけ接続する（名前解決の差し替えによる回避を防ぐ）。TLS 検証は既定のまま。
    /// </summary>
    public static SocketsHttpHandler CreateDefaultHandler() => CreateDefaultHandler(DestinationPolicy.Default);

    /// <summary>接続時の宛先検査に <paramref name="policy"/> を使うハンドラー。</summary>
    public static SocketsHttpHandler CreateDefaultHandler(DestinationPolicy policy) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = (context, token) => ConnectGuardedAsync(context, policy, token),
    };

    /// <summary>フィードを取得する。</summary>
    /// <param name="url">取得先（絶対 HTTPS。userinfo・fragment 不可）。</param>
    /// <param name="authentication">認証設定。秘密は呼び出し時に解決する。</param>
    /// <param name="validators">前回の検証子。あれば条件付きで取得する。</param>
    /// <param name="environment">秘密の解決に使う環境変数の読み取り。省略時は実環境。</param>
    /// <exception cref="FeedFetchException">取得できなかった。</exception>
    public async Task<FeedFetchResult> FetchAsync(
        Uri url,
        AuthenticationConfiguration authentication,
        FeedValidators? validators = null,
        Func<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(authentication);
        ValidateStart(url);
        var credentials = BuildCredentials(authentication, environment);

        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(_options.TotalTimeout);
        try
        {
            return await FetchCoreAsync(url, credentials, validators, total.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FeedFetchException(FeedFetchErrorKind.Timeout, null, "取得全体が時間切れになりました。");
        }
    }

    private async Task<FeedFetchResult> FetchCoreAsync(
        Uri start, List<(string Name, string Value)> credentials, FeedValidators? validators, CancellationToken total)
    {
        var current = start;
        for (var redirects = 0; ; redirects++)
        {
            var hop = await SendWithRetryAsync(current, credentials, validators, total).ConfigureAwait(false);
            if (hop.Location is null)
            {
                return hop.Result! with { FinalUrl = current };
            }

            if (redirects >= _options.MaxRedirects)
            {
                throw new FeedFetchException(FeedFetchErrorKind.Redirect, null, "リダイレクトの回数が上限を超えました。");
            }

            current = ResolveRedirect(current, hop.Location);
        }
    }

    private static Uri ResolveRedirect(Uri current, Uri location)
    {
        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (next.Scheme != Uri.UriSchemeHttps)
        {
            throw new FeedFetchException(FeedFetchErrorKind.Redirect, null, "HTTPS 以外へのリダイレクトは拒否します。");
        }

        if (!string.IsNullOrEmpty(next.UserInfo))
        {
            throw new FeedFetchException(FeedFetchErrorKind.Redirect, null, "資格情報を含むリダイレクトは拒否します。");
        }

        if (!SameOrigin(current, next))
        {
            throw new FeedFetchException(FeedFetchErrorKind.Redirect, null, "別の origin へのリダイレクトは拒否します。");
        }

        // fragment は送られないので落とす
        return new UriBuilder(next) { Fragment = string.Empty }.Uri;
    }

    private static bool SameOrigin(Uri a, Uri b)
        => a.Scheme == b.Scheme
            && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port;

    private static void ValidateStart(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new FeedFetchException(FeedFetchErrorKind.Destination, null, "取得先は絶対 HTTPS URL が必要です。");
        }

        if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment))
        {
            throw new FeedFetchException(FeedFetchErrorKind.Destination, null, "取得先に userinfo・fragment は使えません。");
        }
    }

    private sealed record Hop(FeedFetchResult? Result, Uri? Location);

    private async Task<Hop> SendWithRetryAsync(
        Uri url, List<(string Name, string Value)> credentials, FeedValidators? validators, CancellationToken total)
    {
        await DestinationGuard.ResolveAllowedAsync(url.IdnHost, _resolver, _policy, total).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            FeedFetchException failure;
            try
            {
                return await SendOnceAsync(url, credentials, validators, total).ConfigureAwait(false);
            }
            catch (RetryableException ex)
            {
                failure = ex.Failure;
                retryAfter = ex.RetryAfter;
            }

            if (attempt >= _options.MaxRetries)
            {
                throw failure;
            }

            var wait = TimeSpan.FromSeconds(BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)]);
            if (retryAfter is { } requested)
            {
                if (requested > _options.MaxRetryAfter)
                {
                    throw new FeedFetchException(
                        FeedFetchErrorKind.RateLimit, failure.StatusCode, "待機指定が長いため、次回へ回します。");
                }

                wait = requested > wait ? requested : wait;
            }

            await _delay(wait, total).ConfigureAwait(false);
        }
    }

    private sealed class RetryableException(FeedFetchException failure, TimeSpan? retryAfter) : Exception
    {
        public FeedFetchException Failure { get; } = failure;

        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private async Task<Hop> SendOnceAsync(
        Uri url, List<(string Name, string Value)> credentials, FeedValidators? validators, CancellationToken total)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(
            "Accept", "application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.5");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        foreach (var (name, value) in credentials)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var conditional = false;
        if (validators?.ETag is { Length: > 0 } etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            conditional = true;
        }

        if (validators?.LastModified is { } modified)
        {
            request.Headers.TryAddWithoutValidation(
                "If-Modified-Since", modified.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture));
            conditional = true;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(total);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            return await HandleAsync(response, url, validators, conditional, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new RetryableException(
                new FeedFetchException(FeedFetchErrorKind.Transport, null, "通信に失敗しました。"), null);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !total.IsCancellationRequested)
        {
            throw new RetryableException(
                new FeedFetchException(FeedFetchErrorKind.Timeout, null, "要求が時間切れになりました。"), null);
        }
    }

    private async Task<Hop> HandleAsync(
        HttpResponseMessage response, Uri url, FeedValidators? sent, bool conditional, CancellationToken token)
    {
        var status = (int)response.StatusCode;
        switch (status)
        {
            case 200:
                return new Hop(await ReadBodyAsync(response, url, token).ConfigureAwait(false), null);
            case 304 when conditional:
                return new Hop(
                    new FeedFetchResult(
                        null,
                        true,
                        url,
                        null,
                        new FeedValidators(
                            response.Headers.ETag?.ToString() ?? sent?.ETag,
                            response.Content.Headers.LastModified ?? sent?.LastModified)),
                    null);
            case 301 or 302 or 303 or 307 or 308:
                var location = response.Headers.Location
                    ?? throw new FeedFetchException(FeedFetchErrorKind.Redirect, status, "Location がありません。");
                return new Hop(null, location);
            case 401 or 403:
                throw new FeedFetchException(FeedFetchErrorKind.Authentication, status, "認証・認可に失敗しました。");
            case 429:
                throw new RetryableException(
                    new FeedFetchException(FeedFetchErrorKind.RateLimit, status, "要求が多すぎます。"),
                    ParseRetryAfter(response));
            case 503:
                throw new RetryableException(
                    new FeedFetchException(FeedFetchErrorKind.HttpStatus, status, "サーバーが一時的に使えません。"),
                    ParseRetryAfter(response));
            case 408 or >= 500:
                throw new RetryableException(
                    new FeedFetchException(FeedFetchErrorKind.HttpStatus, status, "サーバーのエラー応答です。"), null);
            default:
                throw new FeedFetchException(FeedFetchErrorKind.HttpStatus, status, "取得に失敗しました。");
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
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

    private async Task<FeedFetchResult> ReadBodyAsync(HttpResponseMessage response, Uri url, CancellationToken token)
    {
        var limit = _options.MaxBodyBytes;
        if (response.Content.Headers.ContentLength > limit)
        {
            throw Limit();
        }

        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (response.Content.Headers.ContentEncoding.Count > 1
            || encoding is not (null or "" or "identity" or "gzip" or "x-gzip"))
        {
            throw new FeedFetchException(
                FeedFetchErrorKind.UnsupportedEncoding, (int)response.StatusCode, "未対応の Content-Encoding です。");
        }

        await using var raw = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var capped = new CappedStream(raw, limit);
        Stream source = capped;
        if (encoding is "gzip" or "x-gzip")
        {
            source = new GZipStream(capped, CompressionMode.Decompress);
        }

        try
        {
            await using (source)
            {
                var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > limit)
                    {
                        throw Limit();
                    }

                    buffer.Write(chunk, 0, read);
                }

                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'', ' ');
                return new FeedFetchResult(
                    buffer.ToArray(),
                    false,
                    url,
                    string.IsNullOrWhiteSpace(charset) ? null : charset,
                    new FeedValidators(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified));
            }
        }
        catch (InvalidDataException)
        {
            throw new FeedFetchException(
                FeedFetchErrorKind.UnsupportedEncoding, (int)response.StatusCode, "圧縮データが不正です。");
        }
        catch (IOException)
        {
            throw new RetryableException(
                new FeedFetchException(FeedFetchErrorKind.Transport, null, "本文の読み取りに失敗しました。"), null);
        }
    }

    private static FeedFetchException Limit()
        => new(FeedFetchErrorKind.LimitExceeded, null, "本文が容量の上限を超えています。");

    /// <summary>読んだバイト数が上限を超えたら読み取りを止める（圧縮したままの量を数える）。</summary>
    private sealed class CappedStream(Stream inner, int limit) : Stream
    {
        private long _total;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        private int Count(int read)
        {
            _total += read;
            if (_total > limit)
            {
                throw Limit();
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>認証設定から、送るヘッダを作る。秘密は解決のたびに読み、保持しない。</summary>
    private static List<(string Name, string Value)> BuildCredentials(
        AuthenticationConfiguration authentication, Func<string, string?>? environment)
    {
        var headers = new List<(string, string)>();
        switch (authentication.Type)
        {
            case AuthenticationKind.None:
                break;
            case AuthenticationKind.Basic:
                var user = authentication.Username!.Resolve(environment);
                var password = authentication.Password!.Resolve(environment);
                headers.Add((
                    "Authorization",
                    "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"))));
                break;
            case AuthenticationKind.Headers:
                foreach (var header in authentication.Headers)
                {
                    headers.Add((header.Name, header.Value.Resolve(environment)));
                }

                break;
            default:
                throw new InvalidOperationException("未知の認証方式です。");
        }

        return headers;
    }

    private static async Task<IPAddress[]> DefaultResolver(string host, CancellationToken token)
        => await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);

    private static async ValueTask<Stream> ConnectGuardedAsync(
        SocketsHttpConnectionContext context, DestinationPolicy policy, CancellationToken token)
    {
        var addresses = await DestinationGuard
            .ResolveAllowedAsync(context.DnsEndPoint.Host, DefaultResolver, policy, token).ConfigureAwait(false);
        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("接続できません。", last);
    }
}
