using System.Net;
using System.Net.Sockets;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Feeds.Tests;

/// <summary>
/// DNS リバインディング（1 回目の解決で許可内・公開の IP を返し、接続時の再解決で内部・メタデータの IP を返す）の試験。
/// 「検査した IP にだけ接続する」ことを、解決を呼ぶたびに別の IP を返すリゾルバーと、loopback の使い捨てリスナーで確かめる。
/// </summary>
public sealed class DnsRebindingTests
{
    private static readonly AuthenticationConfiguration NoAuth = new(AuthenticationKind.None, null, null, []);

    private static DestinationPolicy Policy(string[] networks, string[] hosts)
        => new(networks.Select(IPNetwork.Parse), hosts);

    /// <summary>呼び出し順に <c>answers</c> の各要素を返すリゾルバー。最後の要素は以降も返す。呼び出し回数を数える。</summary>
    private sealed class SequenceResolver(params string[][] answers)
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
        {
            var index = Math.Min(Interlocked.Increment(ref _calls) - 1, answers.Length - 1);
            return Task.FromResult(answers[index].Select(IPAddress.Parse).ToArray());
        }
    }

    /// <summary>127.0.0.1 の使い捨てリスナー。接続が 1 つでも来たかを数える（来た接続はそのまま閉じる）。</summary>
    private sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;
        private int _accepted;

        public LoopbackListener()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        public Task FirstConnection => _first.Task;

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _accepted);
                    _first.TrySetResult();
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // Dispose による停止
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _loop.GetAwaiter().GetResult();
        }
    }

    private static async Task SettleAsync() => await Task.Delay(300);

    [Fact]
    public async Task 対照_検査を通ったIPへは実際に接続が来る_かつ接続のために再解決しない()
    {
        // 試験の道具が接続を検知できることの確認（これが無いと「接続が来ない」試験が空振りしても気付けない）。
        // 検査した 127.0.0.1（許可内）だけに接続し、2 回目の解決（10.9.9.9 を返す）は呼ばれない
        using var listener = new LoopbackListener();
        var resolver = new SequenceResolver(["127.0.0.1"], ["10.9.9.9"]);
        using var handler = FeedFetcher.CreateDefaultHandler(Policy(["127.0.0.1/32"], ["feed.test"]), resolver.ResolveAsync);
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var request = client.GetAsync($"http://feed.test:{listener.Port}/", cts.Token);
        await listener.FirstConnection.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => request);

        Assert.Equal(1, listener.Accepted);
        Assert.Equal(1, resolver.Calls);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("10.9.9.9")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00:ec2::254")]
    public async Task 接続時に解決が公開IPから内部IPへ変わっても_接続しない_既定の方針(string rebound)
    {
        // 1 回目（事前の検査）は公開 IP、2 回目（接続時）は内部・メタデータ。リスナーは 127.0.0.1 にいるので、
        // 接続時の解決を信じて接続していれば、127.0.0.1 の場合は接続が来てしまう
        using var listener = new LoopbackListener();
        var resolver = new SequenceResolver(["203.0.113.10"], [rebound]);
        using var fetcher = FeedFetcher.CreateDefault(
            new FeedFetchOptions { MaxRetries = 0 }, DestinationPolicy.Default, resolver.ResolveAsync);

        await Assert.ThrowsAsync<FeedFetchException>(
            () => fetcher.FetchAsync(new Uri($"https://feed.test:{listener.Port}/feed"), NoAuth));
        await SettleAsync();

        Assert.Equal(0, listener.Accepted);
        Assert.True(resolver.Calls >= 2, "接続時の検査（2 回目の解決）が行われていない");
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("10.9.9.9")]
    [InlineData("127.0.0.1")]
    [InlineData("fd00:ec2::254")]
    public async Task 接続時に解決が許可内IPから許可外の内部IPへ変わっても_接続しない_許可リストがある方針(string rebound)
    {
        // 1 回目は許可リストの範囲内（10.20.0.5）。2 回目は許可リストの外（メタデータ・別の 10.x・loopback）
        using var listener = new LoopbackListener();
        var resolver = new SequenceResolver(["10.20.0.5"], [rebound]);
        var policy = Policy(["10.20.0.0/16"], ["feed.test"]);
        using var fetcher = FeedFetcher.CreateDefault(new FeedFetchOptions { MaxRetries = 0 }, policy, resolver.ResolveAsync);

        await Assert.ThrowsAsync<FeedFetchException>(
            () => fetcher.FetchAsync(new Uri($"https://feed.test:{listener.Port}/feed"), NoAuth));
        await SettleAsync();

        Assert.Equal(0, listener.Accepted);
        Assert.True(resolver.Calls >= 2, "接続時の検査（2 回目の解決）が行われていない");
    }

    [Fact]
    public async Task 接続時の解決に許可内と内部が混ざっても_接続しない()
    {
        // 接続時の解決が「許可内の IP と、許可外の 127.0.0.1」を両方返す。1 つでも不許可なら全体を拒否する
        using var listener = new LoopbackListener();
        var resolver = new SequenceResolver(["10.20.0.5"], ["10.20.0.5", "127.0.0.1"]);
        var policy = Policy(["10.20.0.0/16"], ["feed.test"]);
        using var handler = FeedFetcher.CreateDefaultHandler(policy, resolver.ResolveAsync);
        using var client = new HttpClient(handler);

        // 事前の検査に相当する 1 回目を使い切ってから、接続時の 2 回目に当たらせる
        _ = await resolver.ResolveAsync("feed.test", CancellationToken.None);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync($"http://feed.test:{listener.Port}/"));
        await SettleAsync();

        Assert.Equal(0, listener.Accepted);
    }

    [Fact]
    public async Task 検査を通った一覧だけが返り_検査のたびに別の解決結果なら_その回の結果で判定する()
    {
        var resolver = new SequenceResolver(["203.0.113.10"], ["169.254.169.254"]);

        var first = await DestinationGuard.ResolveAllowedAsync(
            "feed.test", resolver.ResolveAsync, DestinationPolicy.Default, CancellationToken.None);
        var error = await Assert.ThrowsAsync<FeedFetchException>(() => DestinationGuard.ResolveAllowedAsync(
            "feed.test", resolver.ResolveAsync, DestinationPolicy.Default, CancellationToken.None));

        Assert.Equal([IPAddress.Parse("203.0.113.10")], first);
        Assert.Equal(FeedFetchErrorKind.Destination, error.Kind);
        Assert.Equal(2, resolver.Calls);
        Assert.DoesNotContain("169.254", error.Message);
    }
}
