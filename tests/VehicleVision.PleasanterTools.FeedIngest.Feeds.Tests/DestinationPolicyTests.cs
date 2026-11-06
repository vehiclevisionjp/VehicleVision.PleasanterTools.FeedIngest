using System.Net;
using System.Net.Http.Headers;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds.Tests;

/// <summary>内部ホストの明示許可（U11）。許可は source ごとの設定に明示したものだけで、解決後の IP も毎回検査する。</summary>
public sealed class DestinationPolicyTests
{
    private static readonly AuthenticationConfiguration NoAuth = new(AuthenticationKind.None, null, null, []);

    private static DestinationPolicy Policy(string[] networks, string[] hosts)
        => new(networks.Select(IPNetwork.Parse), hosts);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("<rss/>"u8.ToArray()) };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/rss+xml; charset=utf-8");
        return response;
    }

    private static HttpResponseMessage Redirect(string location)
        => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolves(params string[] addresses)
        => (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    private static FeedFetcher Create(
        HttpMessageHandler handler, DestinationPolicy? policy, Func<string, CancellationToken, Task<IPAddress[]>> resolver)
        => new(handler, null, resolver, (_, _) => Task.CompletedTask, policy);

    private static async Task<FeedFetchErrorKind> FailKindAsync(FeedFetcher fetcher, string url)
        => (await Assert.ThrowsAsync<FeedFetchException>(() => fetcher.FetchAsync(new Uri(url), NoAuth))).Kind;

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("172.16.5.5")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("100.64.0.1")]
    public void 既定の空の許可リストでは内部宛を拒否する(string address)
    {
        Assert.False(DestinationPolicy.Default.IsAllowed("feeds.corp.example", IPAddress.Parse(address)));
        Assert.False(DestinationPolicy.Default.IsAllowed(address, IPAddress.Parse(address)));
    }

    [Fact]
    public void 公開アドレスは許可リストが空でも許す()
    {
        Assert.True(DestinationPolicy.Default.IsAllowed("example.test", IPAddress.Parse("93.184.216.34")));
        Assert.True(DestinationPolicy.Default.IsAllowed("example.test", IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")));
    }

    [Fact]
    public void 範囲に入るIPでも_ホストが許可リストに無ければ拒否する()
    {
        var policy = Policy(["10.20.0.0/16"], ["feeds.corp.example"]);

        Assert.True(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("10.20.1.1")));
        Assert.False(policy.IsAllowed("other.corp.example", IPAddress.Parse("10.20.1.1")));
    }

    [Fact]
    public void ホストが許可リストにあっても_解決後のIPが範囲に入らなければ拒否する()
    {
        var policy = Policy(["10.20.0.0/16"], ["feeds.corp.example"]);

        Assert.False(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("10.21.0.1")));
        Assert.False(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("192.168.0.1")));
        Assert.False(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("127.0.0.1")));
    }

    [Fact]
    public void ホストだけ_範囲だけの許可では内部宛を許さない()
    {
        Assert.False(Policy([], ["feeds.corp.example"]).IsAllowed("feeds.corp.example", IPAddress.Parse("10.0.0.1")));
        Assert.False(Policy(["10.0.0.0/8"], []).IsAllowed("feeds.corp.example", IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void ホスト名の比較は大文字小文字を区別せず_部分一致しない()
    {
        var policy = Policy(["10.0.0.0/8"], ["Feeds.Corp.Example"]);

        Assert.True(policy.IsAllowed("FEEDS.corp.example", IPAddress.Parse("10.0.0.1")));
        Assert.False(policy.IsAllowed("evil-feeds.corp.example", IPAddress.Parse("10.0.0.1")));
        Assert.False(policy.IsAllowed("feeds.corp.example.evil.test", IPAddress.Parse("10.0.0.1")));
        Assert.False(policy.IsAllowed("corp.example", IPAddress.Parse("10.0.0.1")));
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.10.10")]
    [InlineData("100.100.100.200")]
    [InlineData("fe80::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("0.0.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void メタデータなどの常に拒否の宛先は_許可リストに書いても拒否する(string address)
    {
        // 範囲は設定の検証で拒否されるが、広い範囲に含まれる場合でも実行時に拒否する
        var policy = Policy(["100.64.0.0/10", "169.0.0.0/8", "fe00::/7", "fd00::/8"], ["feeds.corp.example"]);

        Assert.False(policy.IsAllowed("feeds.corp.example", IPAddress.Parse(address)));
    }

    [Fact]
    public void IPv4射影のIPv6は_IPv4として評価する()
    {
        var policy = Policy(["10.20.0.0/16"], ["feeds.corp.example"]);

        Assert.True(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("::ffff:10.20.1.1")));
        Assert.False(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("::ffff:10.21.1.1")));
    }

    [Fact]
    public void IPv6の範囲を許可できる_範囲外は拒否する()
    {
        var policy = Policy(["fd12:3456::/32", "::1/128"], ["feeds.corp.example"]);

        Assert.True(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("fd12:3456::5")));
        Assert.True(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("::1")));
        Assert.False(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("fd12:3457::5")));
        Assert.False(policy.IsAllowed("feeds.corp.example", IPAddress.Parse("fd00::5")));
    }

    [Fact]
    public void CIDRの境界_32は1アドレスだけ_24は末尾まで()
    {
        var single = Policy(["10.0.0.5/32"], ["h.example"]);
        var block = Policy(["10.0.0.0/24"], ["h.example"]);

        Assert.True(single.IsAllowed("h.example", IPAddress.Parse("10.0.0.5")));
        Assert.False(single.IsAllowed("h.example", IPAddress.Parse("10.0.0.4")));
        Assert.False(single.IsAllowed("h.example", IPAddress.Parse("10.0.0.6")));
        Assert.True(block.IsAllowed("h.example", IPAddress.Parse("10.0.0.0")));
        Assert.True(block.IsAllowed("h.example", IPAddress.Parse("10.0.0.255")));
        Assert.False(block.IsAllowed("h.example", IPAddress.Parse("10.0.1.0")));
    }

    [Fact]
    public void IPリテラルのURLは_リテラルがホストの許可リストにも範囲にもあるときだけ許す()
    {
        Assert.True(Policy(["10.0.0.0/8"], ["10.0.0.5"]).IsAllowed("10.0.0.5", IPAddress.Parse("10.0.0.5")));
        Assert.False(Policy(["10.0.0.0/8"], ["feeds.corp.example"]).IsAllowed("10.0.0.5", IPAddress.Parse("10.0.0.5")));
        Assert.True(Policy(["::1/128"], ["::1"]).IsAllowed("[::1]", IPAddress.IPv6Loopback));
    }

    [Fact]
    public void 設定から作る_空なら既定を使う()
    {
        var empty = new HttpConfiguration(30, 5, 100, NoAuth, [], []);
        var listed = new HttpConfiguration(30, 5, 100, NoAuth, [IPNetwork.Parse("10.0.0.0/8")], ["h.example"]);

        Assert.Same(DestinationPolicy.Default, DestinationPolicy.From(empty));
        Assert.True(DestinationPolicy.From(listed).IsAllowed("h.example", IPAddress.Parse("10.1.1.1")));
    }

    [Fact]
    public void 不正なホストでは作れない()
    {
        Assert.Throws<ArgumentException>(() => Policy([], ["https://h.example"]));
    }

    // FeedFetcher 経由

    [Fact]
    public async Task 既定では内部宛へ解決されるホストへ送信しない()
    {
        var handler = new StubHandler(_ => Ok());
        using var fetcher = Create(handler, null, Resolves("10.20.1.1"));

        Assert.Equal(FeedFetchErrorKind.Destination, await FailKindAsync(fetcher, "https://feeds.corp.example/f.xml"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 許可した範囲とホストなら内部宛でも取得する()
    {
        var handler = new StubHandler(_ => Ok());
        using var fetcher = Create(handler, Policy(["10.20.0.0/16"], ["feeds.corp.example"]), Resolves("10.20.1.1"));

        var result = await fetcher.FetchAsync(new Uri("https://feeds.corp.example/f.xml"), NoAuth);

        Assert.NotNull(result.Body);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 範囲に入らない内部宛は許可リストがあっても拒否する()
    {
        var handler = new StubHandler(_ => Ok());
        using var fetcher = Create(handler, Policy(["10.20.0.0/16"], ["feeds.corp.example"]), Resolves("192.168.1.1"));

        Assert.Equal(FeedFetchErrorKind.Destination, await FailKindAsync(fetcher, "https://feeds.corp.example/f.xml"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 許可リストに無いホストは_範囲に入るIPへ解決されても拒否する()
    {
        var handler = new StubHandler(_ => Ok());
        using var fetcher = Create(handler, Policy(["10.20.0.0/16"], ["feeds.corp.example"]), Resolves("10.20.1.1"));

        Assert.Equal(FeedFetchErrorKind.Destination, await FailKindAsync(fetcher, "https://evil.example/f.xml"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 解決結果に範囲外が1つでも混じれば拒否する()
    {
        var handler = new StubHandler(_ => Ok());
        using var fetcher = Create(
            handler, Policy(["10.20.0.0/16"], ["feeds.corp.example"]), Resolves("10.20.1.1", "10.99.0.1"));

        Assert.Equal(FeedFetchErrorKind.Destination, await FailKindAsync(fetcher, "https://feeds.corp.example/f.xml"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 許可したホストでも_再解決でメタデータへ変わったら拒否する()
    {
        // 1 回目は許可の範囲、リダイレクト後の再解決（DNS リバインディング）はメタデータ
        var calls = 0;
        var handler = new StubHandler(_ => Redirect("/moved.xml"));
        using var fetcher = Create(
            handler,
            Policy(["10.20.0.0/16", "169.0.0.0/8"], ["feeds.corp.example"]),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(++calls == 1 ? "10.20.1.1" : "169.254.169.254") }));

        Assert.Equal(FeedFetchErrorKind.Destination, await FailKindAsync(fetcher, "https://feeds.corp.example/f.xml"));
        Assert.Single(handler.Requests);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task リダイレクトのたびに許可の検査をする_同じ許可の範囲内なら追従する()
    {
        var calls = 0;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/f.xml" ? Redirect("/moved.xml") : Ok());
        using var fetcher = Create(
            handler,
            Policy(["10.20.0.0/16"], ["feeds.corp.example"]),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new[] { IPAddress.Parse("10.20.1.1") });
            });

        var result = await fetcher.FetchAsync(new Uri("https://feeds.corp.example/f.xml"), NoAuth);

        Assert.NotNull(result.Body);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 許可は別originへのリダイレクトを広げない()
    {
        var handler = new StubHandler(_ => Redirect("https://other.corp.example/x.xml"));
        using var fetcher = Create(
            handler, Policy(["10.20.0.0/16"], ["feeds.corp.example", "other.corp.example"]), Resolves("10.20.1.1"));

        Assert.Equal(FeedFetchErrorKind.Redirect, await FailKindAsync(fetcher, "https://feeds.corp.example/f.xml"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 許可は他のsourceの取得器へ漏れない()
    {
        var handler = new StubHandler(_ => Ok());
        using var allowed = Create(handler, Policy(["10.20.0.0/16"], ["feeds.corp.example"]), Resolves("10.20.1.1"));
        using var other = Create(handler, DestinationPolicy.Default, Resolves("10.20.1.1"));
        using var otherListed = Create(handler, Policy(["10.30.0.0/16"], ["feeds.corp.example"]), Resolves("10.20.1.1"));

        Assert.NotNull((await allowed.FetchAsync(new Uri("https://feeds.corp.example/f.xml"), NoAuth)).Body);
        Assert.Equal(FeedFetchErrorKind.Destination, await FailKindAsync(other, "https://feeds.corp.example/f.xml"));
        Assert.Equal(FeedFetchErrorKind.Destination, await FailKindAsync(otherListed, "https://feeds.corp.example/f.xml"));
    }

    [Fact]
    public async Task 内部のIPリテラルのURLは_ホストと範囲の両方に書いたときだけ取得する()
    {
        var handler = new StubHandler(_ => Ok());
        using var both = Create(handler, Policy(["10.20.0.0/16"], ["10.20.1.1"]), (_, _) => throw new InvalidOperationException());
        using var networkOnly = Create(handler, Policy(["10.20.0.0/16"], []), (_, _) => throw new InvalidOperationException());

        Assert.NotNull((await both.FetchAsync(new Uri("https://10.20.1.1/f.xml"), NoAuth)).Body);
        Assert.Equal(FeedFetchErrorKind.Destination, await FailKindAsync(networkOnly, "https://10.20.1.1/f.xml"));
    }

    [Fact]
    public async Task 拒否の分類と文言に_許可リストの内容や解決したIPを含めない()
    {
        var handler = new StubHandler(_ => Ok());
        using var fetcher = Create(handler, Policy(["10.20.0.0/16"], ["feeds.corp.example"]), Resolves("192.168.77.88"));

        var error = await Assert.ThrowsAsync<FeedFetchException>(
            () => fetcher.FetchAsync(new Uri("https://feeds.corp.example/f.xml"), NoAuth));

        Assert.Equal(FeedFetchErrorKind.Destination, error.Kind);
        foreach (var secret in new[] { "192.168.77.88", "10.20", "feeds.corp.example" })
        {
            Assert.DoesNotContain(secret, error.Message);
        }
    }

    [Fact]
    public void 本番用の取得器は_sourceごとの専用のハンドラーを持つ()
    {
        using var a = FeedFetcher.CreateDefault(new FeedFetchOptions(), Policy(["10.0.0.0/8"], ["h.example"]));
        using var b = FeedFetcher.CreateDefault(new FeedFetchOptions(), DestinationPolicy.Default);

        Assert.NotNull(a);
        Assert.NotNull(b);
    }
}
