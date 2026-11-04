using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;
using VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.FeedIngest.Pleasanter.Tests;

public sealed class ExternalKeyUpserterTests
{
    private const long SiteId = 12345;
    private const string Key = "fi:v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly PleasanterOptions Options = new() { BaseUrl = "https://pleasanter.example.test", ApiKey = "secret-api-key" };

    /// <summary>メモリ上の Pleasanter。Get／Create／Update を最小限に模し、失敗は hook で注入する。</summary>
    private sealed class FakePleasanter(int pageSize = 200) : HttpMessageHandler
    {
        public List<JsonObject> Rows { get; } = [];

        public List<(string Path, JsonObject Body)> Calls { get; } = [];

        /// <summary>null を返せば既定の動作。呼び出しごとの通し番号（0 始まり）を渡す。</summary>
        public Func<string, JsonObject, int, HttpResponseMessage?>? Hook { get; set; }

        public long NextId { get; set; } = 1000;

        public int Count(string action) => Calls.Count(call => call.Path.EndsWith("/" + action, StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = (JsonObject)JsonNode.Parse(request.Content!.ReadAsStringAsync(cancellationToken).Result)!;
            var index = Calls.Count;
            Calls.Add((path, body));
            if (Hook?.Invoke(path, body, index) is { } injected)
            {
                return Task.FromResult(injected);
            }

            return Task.FromResult(Default(path, body));
        }

        private HttpResponseMessage Default(string path, JsonObject body)
        {
            if (path.EndsWith("/Get", StringComparison.Ordinal))
            {
                var view = body["View"]!.AsObject();
                var filter = view["ColumnFilterHash"]!.AsObject().Single();
                var matches = Rows
                    .Where(row => (string?)row["ClassHash"]?[filter.Key] == (string?)filter.Value)
                    .ToList();
                var offset = (int?)body["Offset"] ?? 0;
                var page = new JsonArray(matches.Skip(offset).Take(pageSize).Select(row => (JsonNode)row.DeepClone()).ToArray());
                return Json(new JsonObject
                {
                    ["StatusCode"] = 200,
                    ["Response"] = new JsonObject
                    {
                        ["Offset"] = offset,
                        ["PageSize"] = pageSize,
                        ["TotalCount"] = matches.Count,
                        ["Data"] = page,
                    },
                });
            }

            if (path.EndsWith("/Create", StringComparison.Ordinal))
            {
                var row = new JsonObject { ["SiteId"] = SiteId, ["ResultId"] = NextId++ };
                Merge(row, body);
                Rows.Add(row);
                return Json(new JsonObject { ["Id"] = (long)row["ResultId"]!, ["StatusCode"] = 200, ["Message"] = "作成しました" });
            }

            var id = long.Parse(path.Split('/')[^2]);
            var target = Rows.Single(row => (long)row["ResultId"]! == id);
            Merge(target, body);
            return Json(new JsonObject { ["Id"] = id, ["StatusCode"] = 200 });
        }

        private static void Merge(JsonObject row, JsonObject body)
        {
            foreach (var (name, value) in body)
            {
                if (name is "ApiKey" or "ApiVersion" || value is null)
                {
                    continue;
                }

                if (value is JsonObject hash)
                {
                    var existing = row[name] as JsonObject ?? new JsonObject();
                    foreach (var (column, columnValue) in hash)
                    {
                        existing[column] = columnValue?.DeepClone();
                    }

                    row[name] = existing;
                }
                else
                {
                    row[name] = value.DeepClone();
                }
            }
        }
    }

    private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = Json(new JsonObject { ["StatusCode"] = (int)status }, status);
        if (retryAfter is { } span)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(span);
        }

        return response;
    }

    private static HttpResponseMessage Fail(Exception exception) => throw exception;

    private static MappedRecord Record(string title = "題名", string key = Key)
        => new("ClassZ", key, [
            KeyValuePair.Create("Title", (object)title),
            KeyValuePair.Create("ClassA", (object)"https://news.example.test/a"),
            KeyValuePair.Create("DateA", (object)"2026-10-22T09:00:00"),
            KeyValuePair.Create("NumA", (object)1.5m),
            KeyValuePair.Create("CheckA", (object)true),
        ]);

    private static JsonObject ExistingRow(long id, string title = "題名", string key = Key, string? date = "2026-10-22T09:00:00")
        => new()
        {
            ["SiteId"] = SiteId,
            ["ResultId"] = id,
            ["Title"] = title,
            ["ClassHash"] = new JsonObject { ["ClassA"] = "https://news.example.test/a", ["ClassZ"] = key },
            ["DateHash"] = new JsonObject { ["DateA"] = date },
            ["NumHash"] = new JsonObject { ["NumA"] = 1.5 },
            ["CheckHash"] = new JsonObject { ["CheckA"] = true },
        };

    private static (ExternalKeyUpserter Upserter, List<TimeSpan> Delays) Create(
        FakePleasanter server, UpsertOptions? options = null, TimeProvider? time = null)
    {
        var delays = new List<TimeSpan>();
        var client = new PleasanterGateway(server, Options);
        var upserter = new ExternalKeyUpserter(
            client,
            options ?? new UpsertOptions { MinInterval = TimeSpan.Zero },
            (span, _) =>
            {
                delays.Add(span);
                return Task.CompletedTask;
            },
            time);
        return (upserter, delays);
    }

    // 作成・更新

    [Fact]
    public async Task 未作成なら外部キー検索の後に作成し外部キーを同じ要求で保存する()
    {
        var server = new FakePleasanter();
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Created, result.Outcome);
        Assert.Equal(1000, result.RecordId);
        Assert.Equal(["/api/items/12345/Get", "/api/items/12345/Create"], server.Calls.Select(c => c.Path));
        var search = server.Calls[0].Body;
        Assert.Equal(Key, (string?)search["View"]!["ColumnFilterHash"]!["ClassZ"]);
        Assert.Equal(2, (int?)search["View"]!["ColumnFilterSearchTypes"]!["ClassZ"]);
        Assert.Equal(0, (int?)search["Offset"]);
        var create = server.Calls[1].Body;
        Assert.Equal(Key, (string?)create["ClassHash"]!["ClassZ"]);
        Assert.Equal("題名", (string?)create["Title"]);
        Assert.Equal(1.5m, (decimal?)create["NumHash"]!["NumA"]);
        Assert.Equal("secret-api-key", (string?)create["ApiKey"]);
    }

    [Fact]
    public async Task 同じ記事を2回取り込んでもCreateは1回で2回目は何も送らない()
    {
        var server = new FakePleasanter();
        var (upserter, _) = Create(server);

        var first = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);
        var second = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Created, first.Outcome);
        Assert.Equal(UpsertOutcome.Unchanged, second.Outcome);
        Assert.Equal(1, server.Count("Create"));
        Assert.Equal(0, server.Count("Update"));
        Assert.Single(server.Rows);
    }

    [Fact]
    public async Task 内容が変われば所有列だけを更新し外部キー列とApiKey以外の列には触れない()
    {
        var server = new FakePleasanter();
        var row = ExistingRow(7);
        row["Body"] = "利用者が書いた本文";
        server.Rows.Add(row);
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(title: "新しい題名"), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Updated, result.Outcome);
        Assert.Equal(7, result.RecordId);
        var update = server.Calls.Single(c => c.Path.EndsWith("/Update", StringComparison.Ordinal));
        Assert.Equal("/api/items/7/Update", update.Path);
        Assert.Equal("新しい題名", (string?)update.Body["Title"]);
        Assert.Null(update.Body["Body"]);
        Assert.DoesNotContain("ClassZ", update.Body["ClassHash"]!.AsObject().Select(p => p.Key));
        Assert.Equal("利用者が書いた本文", (string?)server.Rows[0]["Body"]);
    }

    [Fact]
    public async Task 所有列が同じなら送らない_日付と数値の表記差も同じとみなす()
    {
        var server = new FakePleasanter();
        server.Rows.Add(ExistingRow(7, date: "2026-10-22T09:00:00.0000000"));
        server.Rows[0]["NumHash"]!["NumA"] = "1.50";
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Unchanged, result.Outcome);
        Assert.Equal(0, server.Count("Update"));
    }

    [Fact]
    public async Task createOnlyは既存に触らない()
    {
        var server = new FakePleasanter();
        server.Rows.Add(ExistingRow(7, title: "古い題名"));
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(title: "新しい"), TargetMode.CreateOnly);

        Assert.Equal(UpsertOutcome.SkippedExisting, result.Outcome);
        Assert.Equal(7, result.RecordId);
        Assert.Equal(0, server.Count("Update"));
        Assert.Equal(0, server.Count("Create"));
    }

    [Fact]
    public async Task 異なるsourceの外部キーは別の記事として扱う()
    {
        var server = new FakePleasanter();
        var (upserter, _) = Create(server);

        await upserter.UpsertAsync(SiteId, Record(key: "fi:v1:aaa"), TargetMode.Upsert);
        await upserter.UpsertAsync(SiteId, Record(key: "fi:v1:bbb"), TargetMode.Upsert);

        Assert.Equal(2, server.Count("Create"));
    }

    // 検索の検証

    [Fact]
    public async Task 外部キーが複数件なら保留して書き込まない()
    {
        var server = new FakePleasanter();
        server.Rows.Add(ExistingRow(1));
        server.Rows.Add(ExistingRow(2));
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Held, result.Outcome);
        Assert.Equal(0, server.Count("Create") + server.Count("Update"));
    }

    [Fact]
    public async Task 部分一致で混入した行は序数比較で除く()
    {
        var server = new FakePleasanter { Hook = (path, _, _) => path.EndsWith("/Get", StringComparison.Ordinal)
            ? Json(new JsonObject
            {
                ["StatusCode"] = 200,
                ["Response"] = new JsonObject
                {
                    ["Offset"] = 0, ["PageSize"] = 200, ["TotalCount"] = 2,
                    ["Data"] = new JsonArray(ExistingRow(1, key: Key + "x"), ExistingRow(2, key: Key.ToUpperInvariant())),
                },
            })
            : null };
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task 全ページを走査して後ろのページの一致を見つける()
    {
        var server = new FakePleasanter(pageSize: 2);
        server.Rows.Add(ExistingRow(1));
        server.Rows.Add(ExistingRow(2));
        server.Rows.Add(ExistingRow(3));
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Held, result.Outcome);
        Assert.Equal([0, 2], server.Calls.Select(c => (int?)c.Body["Offset"] ?? 0));
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("notfound")]
    [InlineData("invalid-json")]
    [InlineData("no-data")]
    [InlineData("no-total")]
    [InlineData("api-status-400")]
    [InlineData("missing-page")]
    [InlineData("match-without-id")]
    public async Task 検索の失敗や応答の欠落は0件とみなさずCreateしない(string kind)
    {
        var server = new FakePleasanter
        {
            Hook = (path, _, _) => !path.EndsWith("/Get", StringComparison.Ordinal)
                ? null
                : kind switch
                {
                    "forbidden" => Status(HttpStatusCode.Forbidden),
                    "notfound" => Status(HttpStatusCode.NotFound),
                    "invalid-json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") },
                    "no-data" => Json(new JsonObject { ["StatusCode"] = 200, ["Response"] = new JsonObject { ["TotalCount"] = 0 } }),
                    "no-total" => Json(new JsonObject { ["StatusCode"] = 200, ["Response"] = new JsonObject { ["Data"] = new JsonArray() } }),
                    "api-status-400" => Json(new JsonObject { ["StatusCode"] = 400 }),
                    "missing-page" => Json(new JsonObject
                    {
                        ["StatusCode"] = 200,
                        ["Response"] = new JsonObject { ["TotalCount"] = 5, ["Data"] = new JsonArray() },
                    }),
                    _ => Json(new JsonObject
                    {
                        ["StatusCode"] = 200,
                        ["Response"] = new JsonObject
                        {
                            ["TotalCount"] = 1,
                            ["Data"] = new JsonArray(new JsonObject { ["ClassHash"] = new JsonObject { ["ClassZ"] = Key } }),
                        },
                    }),
                },
        };
        var (upserter, _) = Create(server, new UpsertOptions { MinInterval = TimeSpan.Zero, MaxRetries = 0 });

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Failed, result.Outcome);
        Assert.Equal(0, server.Count("Create"));
    }

    [Fact]
    public async Task 別のサイトの行は一致として数えない()
    {
        var server = new FakePleasanter();
        var other = ExistingRow(5);
        other["SiteId"] = 999;
        server.Rows.Add(other);
        var (upserter, _) = Create(server);

        Assert.Equal(UpsertOutcome.Created, (await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert)).Outcome);
    }

    [Fact]
    public async Task IssuesのIssueIdも読める()
    {
        var server = new FakePleasanter();
        var row = ExistingRow(7);
        row.Remove("ResultId");
        row["IssueId"] = "77";
        server.Rows.Add(row);
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.CreateOnly);

        Assert.Equal(77, result.RecordId);
    }

    // 認証・再試行・回数制限

    [Fact]
    public async Task 認証失敗はこの実行を止める()
    {
        var server = new FakePleasanter { Hook = (_, _, _) => Status(HttpStatusCode.Unauthorized) };
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Unauthorized, result.Outcome);
        Assert.Single(server.Calls);
        Assert.DoesNotContain("secret-api-key", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 読み取りの一時失敗は1_2_4秒待って再試行する()
    {
        var server = new FakePleasanter { Hook = (path, _, index) => index < 3 ? Status(HttpStatusCode.BadGateway) : null };
        var (upserter, delays) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Created, result.Outcome);
        Assert.Equal([1, 2, 4], delays.Select(d => (int)d.TotalSeconds));
        Assert.Equal(4, server.Count("Get"));
    }

    [Fact]
    public async Task 読み取りの再試行を使い切ったら失敗でCreateしない()
    {
        var server = new FakePleasanter { Hook = (_, _, _) => Status(HttpStatusCode.ServiceUnavailable) };
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Failed, result.Outcome);
        Assert.Equal(4, server.Count("Get"));
        Assert.Equal(0, server.Count("Create"));
    }

    [Fact]
    public async Task 応答429のRetryAfterを守り60秒を超えれば次回へ回す()
    {
        var shortWait = new FakePleasanter { Hook = (_, _, index) => index == 0 ? Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)) : null };
        var (upserter, delays) = Create(shortWait);
        Assert.Equal(UpsertOutcome.Created, (await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert)).Outcome);
        Assert.Equal(7, Assert.Single(delays).TotalSeconds);

        var longWait = new FakePleasanter { Hook = (_, _, _) => Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(61)) };
        var (second, _) = Create(longWait);
        Assert.Equal(UpsertOutcome.RateLimited, (await second.UpsertAsync(SiteId, Record(), TargetMode.Upsert)).Outcome);
        Assert.Single(longWait.Calls);
    }

    [Fact]
    public async Task 応答429が続けば次回へ回す()
    {
        var server = new FakePleasanter { Hook = (_, _, _) => Status(HttpStatusCode.TooManyRequests) };
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.RateLimited, result.Outcome);
        Assert.Equal(4, server.Count("Get"));
    }

    // 書き込みの結果不明

    [Fact]
    public async Task 作成の応答が消えても適用済みなら照合で確認しCreateを再送しない()
    {
        var server = new FakePleasanter();
        server.Hook = (path, body, _) =>
        {
            if (!path.EndsWith("/Create", StringComparison.Ordinal))
            {
                return null;
            }

            // 適用はされたが、応答は届かなかった
            server.Rows.Add(ExistingRow(server.NextId++));
            return Fail(new HttpRequestException("connection reset"));
        };
        var (upserter, delays) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Created, result.Outcome);
        Assert.Equal(1000, result.RecordId);
        Assert.Equal(1, server.Count("Create"));
        Assert.Equal(1, Assert.Single(delays).TotalSeconds);
    }

    [Theory]
    [InlineData("5xx")]
    [InlineData("408")]
    [InlineData("429")]
    [InlineData("network")]
    [InlineData("success-without-id")]
    [InlineData("invalid-json")]
    public async Task 作成が未適用のまま不明なら照合を繰り返し0件でも再送せず保留する(string kind)
    {
        var server = new FakePleasanter
        {
            Hook = (path, _, _) => !path.EndsWith("/Create", StringComparison.Ordinal)
                ? null
                : kind switch
                {
                    "5xx" => Status(HttpStatusCode.InternalServerError),
                    "408" => Status(HttpStatusCode.RequestTimeout),
                    "429" => Status(HttpStatusCode.TooManyRequests),
                    "network" => Fail(new HttpRequestException("timeout")),
                    "success-without-id" => Json(new JsonObject { ["StatusCode"] = 200, ["Message"] = "ok" }),
                    _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") },
                },
        };
        var (upserter, delays) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Held, result.Outcome);
        Assert.Equal(1, server.Count("Create"));
        Assert.Equal(5, server.Count("Get"));
        Assert.Equal([1, 2, 4, 4], delays.Select(d => (int)d.TotalSeconds));
        Assert.Empty(server.Rows);
    }

    [Fact]
    public async Task 作成が恒久エラーなら照合せずに失敗にする()
    {
        var server = new FakePleasanter { Hook = (path, _, _) => path.EndsWith("/Create", StringComparison.Ordinal) ? Status(HttpStatusCode.BadRequest) : null };
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Failed, result.Outcome);
        Assert.Equal(1, server.Count("Get"));
    }

    [Fact]
    public async Task HTTPが200でもJSONのStatusCodeが失敗ならCreateの失敗として扱う()
    {
        var server = new FakePleasanter
        {
            Hook = (path, _, _) => path.EndsWith("/Create", StringComparison.Ordinal)
                ? Json(new JsonObject { ["Id"] = 5, ["StatusCode"] = 400, ["Message"] = "invalid" })
                : null,
        };
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task 作成の照合で複数件見つかれば保留する()
    {
        var server = new FakePleasanter();
        server.Hook = (path, _, _) =>
        {
            if (!path.EndsWith("/Create", StringComparison.Ordinal))
            {
                return null;
            }

            server.Rows.Add(ExistingRow(1));
            server.Rows.Add(ExistingRow(2));
            return Status(HttpStatusCode.BadGateway);
        };
        var (upserter, _) = Create(server);

        Assert.Equal(UpsertOutcome.Held, (await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert)).Outcome);
    }

    [Fact]
    public async Task 更新の応答が消えても適用済みなら再送しない()
    {
        var server = new FakePleasanter();
        server.Rows.Add(ExistingRow(7, title: "古い"));
        server.Hook = (path, body, _) =>
        {
            if (!path.EndsWith("/Update", StringComparison.Ordinal))
            {
                return null;
            }

            server.Rows[0]["Title"] = "新しい";
            return Fail(new HttpRequestException("reset"));
        };
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(title: "新しい"), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Updated, result.Outcome);
        Assert.Equal(1, server.Count("Update"));
    }

    [Fact]
    public async Task 更新が未適用なら同じ所有列値だけを再送し続けない()
    {
        var failures = 0;
        var server = new FakePleasanter();
        server.Rows.Add(ExistingRow(7, title: "古い"));
        server.Hook = (path, _, _) => path.EndsWith("/Update", StringComparison.Ordinal) && failures++ < 1
            ? Status(HttpStatusCode.BadGateway)
            : null;
        var (upserter, _) = Create(server);

        Assert.Equal(UpsertOutcome.Updated, (await upserter.UpsertAsync(SiteId, Record(title: "新しい"), TargetMode.Upsert)).Outcome);
        Assert.Equal(2, server.Count("Update"));

        var always = new FakePleasanter { Hook = (path, _, _) => path.EndsWith("/Update", StringComparison.Ordinal) ? Status(HttpStatusCode.BadGateway) : null };
        always.Rows.Add(ExistingRow(7, title: "古い"));
        var (second, _) = Create(always);
        Assert.Equal(UpsertOutcome.Held, (await second.UpsertAsync(SiteId, Record(title: "新しい"), TargetMode.Upsert)).Outcome);
        Assert.Equal(4, always.Count("Update"));
    }

    [Fact]
    public async Task 更新が恒久エラーなら失敗にする()
    {
        var server = new FakePleasanter { Hook = (path, _, _) => path.EndsWith("/Update", StringComparison.Ordinal) ? Status(HttpStatusCode.Forbidden) : null };
        server.Rows.Add(ExistingRow(7, title: "古い"));
        var (upserter, _) = Create(server);

        Assert.Equal(UpsertOutcome.Failed, (await upserter.UpsertAsync(SiteId, Record(title: "新しい"), TargetMode.Upsert)).Outcome);
        Assert.Equal(1, server.Count("Update"));
    }

    // 予算・間隔

    [Fact]
    public async Task 予算を使い切ったら残りを送らず成功に数えない()
    {
        var server = new FakePleasanter();
        var (upserter, _) = Create(server, new UpsertOptions { MinInterval = TimeSpan.Zero, MaxRequests = 2 });

        var first = await upserter.UpsertAsync(SiteId, Record(key: "fi:v1:a"), TargetMode.Upsert);
        var second = await upserter.UpsertAsync(SiteId, Record(key: "fi:v1:b"), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Created, first.Outcome);
        Assert.Equal(UpsertOutcome.BudgetExhausted, second.Outcome);
        Assert.Equal(2, server.Calls.Count);
        Assert.Equal(2, upserter.RequestCount);
    }

    [Fact]
    public async Task 作成の照合の途中で予算が尽きたら保留する()
    {
        var server = new FakePleasanter { Hook = (path, _, _) => path.EndsWith("/Create", StringComparison.Ordinal) ? Status(HttpStatusCode.BadGateway) : null };
        var (upserter, _) = Create(server, new UpsertOptions { MinInterval = TimeSpan.Zero, MaxRequests = 3 });

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.Equal(UpsertOutcome.Held, result.Outcome);
        Assert.Equal(1, server.Count("Create"));
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Now { get; set; } = 1_000_000_000;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Now;
    }

    [Fact]
    public async Task 要求の最低間隔を空ける()
    {
        var server = new FakePleasanter();
        var time = new ManualTime();
        var (upserter, delays) = Create(server, new UpsertOptions { MinInterval = TimeSpan.FromMilliseconds(250) }, time);

        await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        // Get → Create の間で、時間が進んでいなければ 250ms 待つ
        Assert.Equal(250, Assert.Single(delays).TotalMilliseconds);

        delays.Clear();
        time.Now += 300;
        await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);
        Assert.DoesNotContain(delays, d => d.TotalMilliseconds > 250);
    }

    [Fact]
    public async Task 同時要求は1つに制限する()
    {
        var concurrent = 0;
        var peak = 0;
        var server = new FakePleasanter();
        var handler = new DelegatingTracker(server, () => Interlocked.Increment(ref concurrent), () => Interlocked.Decrement(ref concurrent), v => peak = Math.Max(peak, v));
        var upserter = new ExternalKeyUpserter(
            new PleasanterGateway(handler, Options),
            new UpsertOptions { MinInterval = TimeSpan.Zero },
            (_, _) => Task.CompletedTask);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => upserter.UpsertAsync(SiteId, Record(key: "fi:v1:" + i), TargetMode.Upsert)));

        Assert.Equal(1, peak);
    }

    private sealed class DelegatingTracker(HttpMessageHandler inner, Func<int> enter, Func<int> leave, Action<int> observe)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            observe(enter());
            await Task.Yield();
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                leave();
            }
        }
    }

    [Fact]
    public async Task 結果の理由にAPIキーを含めない()
    {
        var server = new FakePleasanter { Hook = (_, _, _) => Status(HttpStatusCode.InternalServerError) };
        var (upserter, _) = Create(server);

        var result = await upserter.UpsertAsync(SiteId, Record(), TargetMode.Upsert);

        Assert.DoesNotContain("secret-api-key", result.Reason ?? string.Empty, StringComparison.Ordinal);
    }
}
