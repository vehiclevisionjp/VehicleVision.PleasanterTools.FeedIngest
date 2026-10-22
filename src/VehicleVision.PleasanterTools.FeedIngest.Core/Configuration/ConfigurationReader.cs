using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

/// <summary>通信せず、設定の構文・型・範囲・宣言間の整合性を検査する。</summary>
public static partial class ConfigurationReader
{
    public static FeedIngestConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(json);
            using var document = JsonDocument.Parse(bytes);
            var locations = new Dictionary<string, int>(StringComparer.Ordinal);
            var starts = new List<int> { 0 };
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n')
                {
                    starts.Add(i + 1);
                }
            }
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            Index(ref reader, "$", starts.ToArray(), locations);
            return Read(new Node(document.RootElement, "$", locations));
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException(checked((int)(ex.LineNumber ?? 0) + 1), "$", "JSON の構文が不正です。");
        }
        catch (EncoderFallbackException)
        {
            throw new ConfigurationException(1, "$", "不正な Unicode 文字列です。");
        }
    }

    private static void Index(
        ref Utf8JsonReader reader, string path, int[] lineStarts, Dictionary<string, int> locations)
    {
        locations.TryAdd(path, Line(lineStarts, reader.TokenStartIndex));
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString()!;
                var child = path + "." + name;
                var line = Line(lineStarts, reader.TokenStartIndex);
                if (!names.Add(name))
                {
                    throw new ConfigurationException(line, child, "プロパティが重複しています。");
                }
                locations[child] = line;
                reader.Read();
                Index(ref reader, child, lineStarts, locations);
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                Index(ref reader, $"{path}[{index++}]", lineStarts, locations);
            }
        }
    }

    private static int Line(int[] lineStarts, long offset)
    {
        var index = System.Array.BinarySearch(lineStarts, checked((int)offset));
        return index >= 0 ? index + 1 : ~index;
    }

    private static FeedIngestConfiguration Read(Node root)
    {
        root.Object("schemaVersion", "pleasanter", "sources");
        var version = root.Required("schemaVersion").Integer(1, 1);
        var destination = root.Required("pleasanter");
        destination.Object("id", "baseUrl", "apiVersion", "apiKeyEnv", "apiKeyUserTimeZoneId");
        var apiVersion = destination.Required("apiVersion").Decimal();
        if (apiVersion != 1.1m)
        {
            throw destination.Required("apiVersion").Error("対応 API 版は 1.1 のみです。");
        }
        var timezone = destination.Required("apiKeyUserTimeZoneId");
        var timezoneId = timezone.Text();
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw timezone.Error("認識できないタイムゾーンです。");
        }
        var target = new DestinationConfiguration(
            destination.Required("id").Text(), destination.Required("baseUrl").HttpsUrl(allowQuery: false),
            apiVersion, destination.Required("apiKeyEnv").Secret(), timezoneId);
        var sources = root.Required("sources").Array();
        if (sources.Count == 0)
        {
            throw root.Required("sources").Error("取り込み元が必要です。");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SourceConfiguration>();
        foreach (var source in sources)
        {
            source.Object("id", "url", "format", "http", "target", "mapping");
            var id = source.Required("id").Text();
            if (!ids.Add(id))
            {
                throw source.Required("id").Error("取り込み元 ID が重複しています。");
            }
            var format = source.Required("format").Choice<FeedFormat>("auto", "rss20", "atom10");
            var http = ReadHttp(source.Required("http"));
            var site = source.Required("target");
            site.Object("siteId", "externalKeyColumn", "mode");
            var siteId = site.Required("siteId").Integer(1, long.MaxValue);
            var external = site.Required("externalKeyColumn");
            if (!ColumnName().IsMatch(external.Text()) || !external.Text().StartsWith("Class", StringComparison.Ordinal))
            {
                throw external.Error("外部キーは Class 列が必要です。");
            }
            var mode = site.Required("mode").Choice<TargetMode>("upsert", "createOnly");
            var mappings = source.Required("mapping").Array();
            if (mappings.Count == 0)
            {
                throw source.Required("mapping").Error("対応づけが必要です。");
            }
            var columns = new HashSet<string>(StringComparer.Ordinal) { external.Text() };
            var rules = new List<MappingConfiguration>();
            foreach (var mapping in mappings)
            {
                rules.Add(ReadMapping(mapping, columns));
            }
            result.Add(new SourceConfiguration(
                id, source.Required("url").HttpsUrl(allowQuery: true), format, http,
                new TargetConfiguration(siteId, external.Text(), mode), rules.AsReadOnly()));
        }
        return new FeedIngestConfiguration((int)version, target, result.AsReadOnly());
    }

    private static HttpConfiguration ReadHttp(Node http)
    {
        http.Object("timeoutSeconds", "maxRedirects", "maxBodyBytes", "authentication");
        var timeout = http.Optional("timeoutSeconds")?.Integer(1, 30) ?? 30;
        var redirects = http.Optional("maxRedirects")?.Integer(0, 5) ?? 5;
        var body = http.Optional("maxBodyBytes")?.Integer(1, 10485760) ?? 10485760;
        var auth = http.Required("authentication");
        auth.Object("type", "usernameEnv", "passwordEnv", "headers");
        var kind = auth.Required("type").Choice<AuthenticationKind>("none", "basic", "headers");
        SecretReference? username = null;
        SecretReference? password = null;
        var headers = new List<HeaderConfiguration>();
        switch (kind)
        {
            case AuthenticationKind.None:
                auth.Forbid("usernameEnv", "passwordEnv", "headers");
                break;
            case AuthenticationKind.Basic:
                auth.Forbid("headers");
                username = auth.Required("usernameEnv").Secret();
                password = auth.Required("passwordEnv").Secret();
                break;
            case AuthenticationKind.Headers:
                auth.Forbid("usernameEnv", "passwordEnv");
                var headerNodes = auth.Required("headers").Array();
                if (headerNodes.Count == 0)
                {
                    throw auth.Required("headers").Error("認証ヘッダが必要です。");
                }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in headerNodes)
                {
                    header.Object("name", "valueEnv");
                    var nameNode = header.Required("name");
                    var name = nameNode.Text();
                    if (!HeaderName().IsMatch(name) || !names.Add(name) || IsReservedHeader(name))
                    {
                        throw nameNode.Error("禁止・重複または不正なヘッダ名です。");
                    }
                    headers.Add(new HeaderConfiguration(name, header.Required("valueEnv").Secret()));
                }
                break;
        }
        return new HttpConfiguration((int)timeout, (int)redirects, (int)body,
            new AuthenticationConfiguration(kind, username, password, headers.AsReadOnly()));
    }

    private static bool IsReservedHeader(string name) =>
        name.StartsWith("If-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
        || new[]
        {
            "Host", "Cookie", "Set-Cookie", "Connection", "Keep-Alive", "Proxy-Connection",
            "Proxy-Authorization", "Proxy-Authenticate", "Transfer-Encoding", "TE", "Trailer",
            "Upgrade", "Expect", "Range", "Accept-Encoding",
        }.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static MappingConfiguration ReadMapping(Node rule, HashSet<string> columns)
    {
        rule.Object("target", "source", "constant", "default", "transform", "separator", "onMissing");
        var target = rule.Required("target");
        var column = target.Text();
        if (!ColumnName().IsMatch(column))
        {
            throw target.Error("未対応の列です。Attachments・システム列は指定できません。");
        }
        if (!columns.Add(column))
        {
            throw target.Error("列が重複、または外部キー予約列です。");
        }
        var source = rule.Optional("source");
        var constant = rule.Optional("constant");
        var fallback = rule.Optional("default");
        if ((source is null) == (constant is null) || (constant is not null && fallback is not null))
        {
            throw rule.Error("source と constant はいずれか一方、constant と default は併用不可です。");
        }
        var transform = rule.Optional("transform")?.Choice<MappingTransform>("identity", "join", "json")
            ?? MappingTransform.Identity;
        var missing = rule.Optional("onMissing")?.Choice<MissingValueBehavior>("omit", "error")
            ?? MissingValueBehavior.Omit;
        var separator = rule.Optional("separator");
        if (transform == MappingTransform.Join)
        {
            _ = rule.Required("separator").Text(allowEmpty: true);
        }
        else if (separator is not null)
        {
            throw separator.Error("separator は join 専用です。");
        }
        if (constant is not null)
        {
            rule.Forbid("transform", "separator", "onMissing");
            ValidateLiteral(constant, column);
        }
        if (source is not null)
        {
            var name = source.Text();
            var isArray = name is "categories" or "authors";
            var isDate = name is "publishedAt" or "updatedAt";
            var isMetadata = name == "enclosures";
            if (!isArray && !isDate && !isMetadata
                && name is not ("title" or "link" or "contentText" or "summaryText" or "nativeId" or "sourceId"))
            {
                throw source.Error("未定義の元項目です。");
            }
            if ((isArray && transform != MappingTransform.Join)
                || (isMetadata && transform != MappingTransform.Json)
                || (!isArray && !isMetadata && transform != MappingTransform.Identity))
            {
                throw source.Error("元項目の型と変換が一致しません。");
            }
            var dateColumn = column.StartsWith("Date", StringComparison.Ordinal);
            if (isDate != dateColumn || column.StartsWith("Num", StringComparison.Ordinal)
                || column.StartsWith("Check", StringComparison.Ordinal))
            {
                throw source.Error("元項目と送信列の型が一致しません。");
            }
        }
        if (fallback is not null)
        {
            ValidateLiteral(fallback, column);
        }
        return new MappingConfiguration(column, source?.Text(), constant?.Value.Clone(), fallback?.Value.Clone(),
            transform, separator?.Text(allowEmpty: true), missing);
    }

    private static void ValidateLiteral(Node node, string column)
    {
        if (column.StartsWith("Num", StringComparison.Ordinal))
        {
            _ = node.Decimal();
        }
        else if (column.StartsWith("Check", StringComparison.Ordinal))
        {
            if (node.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw node.Error("JSON boolean が必要です。");
            }
        }
        else if (column.StartsWith("Date", StringComparison.Ordinal))
        {
            var value = node.Text();
            if (!OffsetDate().IsMatch(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))
            {
                throw node.Error("オフセット付きの ISO 8601 日時が必要です。日付クリアは未対応です。");
            }
        }
        else
        {
            _ = node.Text(allowEmpty: true);
        }
    }

    private sealed class Node(JsonElement value, string path, Dictionary<string, int> locations)
    {
        public JsonElement Value { get; } = value;
        public ConfigurationException Error(string reason) => new(locations.GetValueOrDefault(path, 1), path, reason);
        public Node? Optional(string key) =>
            Value.TryGetProperty(key, out var child) ? new Node(child, path + "." + key, locations) : null;
        public Node Required(string key) => Optional(key)
            ?? throw new ConfigurationException(locations.GetValueOrDefault(path, 1), path + "." + key, "必須項目です。");

        public void Object(params string[] allowed)
        {
            if (Value.ValueKind != JsonValueKind.Object)
            {
                throw Error("オブジェクトが必要です。");
            }
            foreach (var property in Value.EnumerateObject())
            {
                if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                {
                    throw Required(property.Name).Error("未知のプロパティです。");
                }
            }
        }

        public void Forbid(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (Optional(key) is { } node)
                {
                    throw node.Error("この指定では併用できません。");
                }
            }
        }

        public List<Node> Array()
        {
            if (Value.ValueKind != JsonValueKind.Array)
            {
                throw Error("配列が必要です。");
            }
            return Value.EnumerateArray().Select((item, index) =>
                new Node(item, $"{path}[{index}]", locations)).ToList();
        }

        public string Text(bool allowEmpty = false)
        {
            if (Value.ValueKind != JsonValueKind.String)
            {
                throw Error("文字列が必要です。");
            }
            var text = Value.GetString()!;
            if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            {
                throw Error("空でない文字列が必要です。");
            }
            return text;
        }

        public long Integer(long min, long max)
        {
            if (Value.ValueKind != JsonValueKind.Number || !Value.TryGetInt64(out var number)
                || number < min || number > max)
            {
                throw Error($"整数の範囲は {min}〜{max} です。");
            }
            return number;
        }

        public decimal Decimal()
        {
            if (Value.ValueKind != JsonValueKind.Number || !Value.TryGetDecimal(out var number))
            {
                throw Error("decimal の範囲の JSON number が必要です。");
            }
            return number;
        }

        public T Choice<T>(params string[] values) where T : struct, Enum
        {
            var text = Text();
            var index = System.Array.IndexOf(values, text);
            if (index < 0)
            {
                throw Error("未定義の選択値です。");
            }
            return (T)Enum.ToObject(typeof(T), index);
        }

        public SecretReference Secret()
        {
            try
            {
                return new SecretReference(Text());
            }
            catch (InvalidOperationException)
            {
                throw Error("秘密は _FILE を含まない環境変数名で参照してください。");
            }
        }

        public Uri HttpsUrl(bool allowQuery)
        {
            if (!Uri.TryCreate(Text(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || (!allowQuery && uri.Query.Length != 0))
            {
                throw Error("資格情報・fragment を含まない絶対 HTTPS URL が必要です。");
            }
            return uri;
        }
    }

    [GeneratedRegex(@"^(Title|Body|(Class|Date|Description|Num|Check)([A-Z]|[0-9]{3}))$", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnName();

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetDate();
}
