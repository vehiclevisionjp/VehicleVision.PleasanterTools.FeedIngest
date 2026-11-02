using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;

/// <summary>
/// 設定の対応づけを、記事から Pleasanter の列値へ決定的に適用する。通信・時刻・乱数に依存しない。
/// </summary>
/// <remarks>
/// <para>型・欠落の誤りは切り詰め・無断の null 化をせず、記事全体のエラー（保留）にする。
/// サイト固有の制約（文字数・選択肢・日時範囲）は検証できないため適用しない。</para>
/// <para>日時は API キー利用者のタイムゾーンへ変換した <c>yyyy-MM-ddTHH:mm:ss</c> で出す。
/// Pleasanter 実機での解釈は未確認。</para>
/// </remarks>
public sealed class EntryMapper
{
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly IReadOnlyList<MappingConfiguration> _rules;
    private readonly TimeZoneInfo _timeZone;

    public EntryMapper(
        IReadOnlyList<MappingConfiguration> rules, string externalKeyColumn, TimeZoneInfo apiKeyUserTimeZone)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalKeyColumn);
        ArgumentNullException.ThrowIfNull(apiKeyUserTimeZone);
        _rules = rules;
        ExternalKeyColumn = externalKeyColumn;
        _timeZone = apiKeyUserTimeZone;
        Version = ComputeVersion(rules, externalKeyColumn, apiKeyUserTimeZone.Id);
    }

    /// <summary>設定から作る。</summary>
    public EntryMapper(DestinationConfiguration destination, SourceConfiguration source)
        : this(
            source.Mapping,
            source.Target.ExternalKeyColumn,
            TimeZoneInfo.FindSystemTimeZoneById(destination.ApiKeyUserTimeZoneId))
    {
    }

    public string ExternalKeyColumn { get; }

    /// <summary>対応づけ設定の版（SHA-256 の hex）。設定が変われば変わる。</summary>
    public string Version { get; }

    public MappingResult Map(NormalizedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var columns = new Dictionary<string, object>(StringComparer.Ordinal);
        var errors = new List<MappingError>();
        foreach (var rule in _rules)
        {
            try
            {
                var value = Resolve(rule, entry);
                if (value is not null)
                {
                    columns[rule.Target] = value;
                }
            }
            catch (MappingFailure failure)
            {
                errors.Add(new MappingError(rule.Target, failure.Message));
            }
        }

        return errors.Count > 0
            ? new MappingResult(null, errors)
            : new MappingResult(new MappedRecord(ExternalKeyColumn, ExternalKey.For(entry), columns), []);
    }

    private object? Resolve(MappingConfiguration rule, NormalizedEntry entry)
    {
        if (rule.Constant is { } constant)
        {
            return FromLiteral(rule.Target, constant);
        }

        var raw = ReadSource(rule.Source!, entry);
        var converted = Transform(rule, raw);
        if (converted is not null)
        {
            return ToColumnValue(rule.Target, converted);
        }

        if (rule.Default is { } fallback)
        {
            return FromLiteral(rule.Target, fallback);
        }

        if (rule.OnMissing == MissingValueBehavior.Error)
        {
            throw new MappingFailure("必須の元項目が欠落しています。");
        }

        // 欠落は列に触らない（空文字の明示値とは区別する）
        return null;
    }

    private static object? ReadSource(string source, NormalizedEntry entry) => source switch
    {
        "title" => entry.Title,
        "link" => entry.Link?.AbsoluteUri,
        "contentText" => ContentText(entry),
        "summaryText" => entry.Summary,
        "nativeId" => entry.NativeId,
        "sourceId" => entry.SourceId,
        "publishedAt" => entry.PublishedAt,
        "updatedAt" => entry.UpdatedAt,
        "categories" => entry.Categories
            .Select(c => string.IsNullOrWhiteSpace(c.Label) ? c.Term : c.Label).ToArray(),
        "authors" => entry.Authors.Select(a => a.Name).ToArray(),
        "enclosures" => entry.Enclosures,
        _ => throw new MappingFailure("未定義の元項目です。"),
    };

    /// <summary>本文のテキスト。HTML は実行せずテキスト化し、外部・バイナリの本文は欠落として扱う。</summary>
    private static string? ContentText(NormalizedEntry entry) => entry.ContentKind switch
    {
        EntryContentKind.Html or EntryContentKind.Xhtml => HtmlText.ToPlainText(entry.Content),
        EntryContentKind.Text => entry.Content,
        _ => null,
    };

    private object? Transform(MappingConfiguration rule, object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case string text:
                return string.IsNullOrWhiteSpace(text) ? null : text;
            case DateTimeOffset date:
                return date;
            case string[] items:
                var parts = items.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
                return parts.Length == 0 ? null : string.Join(rule.Separator ?? string.Empty, parts);
            case IReadOnlyList<EntryEnclosure> enclosures:
                return enclosures.Count == 0 ? null : EnclosuresJson(enclosures);
            default:
                throw new MappingFailure("元項目の型が不正です。");
        }
    }

    /// <summary>URL・type・length だけの JSON（添付の実体・秘密は含めない）。</summary>
    private static string EnclosuresJson(IReadOnlyList<EntryEnclosure> enclosures)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartArray();
            foreach (var enclosure in enclosures)
            {
                writer.WriteStartObject();
                writer.WriteString("url", enclosure.Url.AbsoluteUri);
                writer.WriteString("type", enclosure.Type);
                if (enclosure.Length is { } length)
                {
                    writer.WriteNumber("length", length);
                }
                else
                {
                    writer.WriteNull("length");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private object ToColumnValue(string column, object value)
    {
        if (value is DateTimeOffset date)
        {
            return FormatDate(date);
        }

        return value is string text && !IsColumn(column, "Num", "Check")
            ? text
            : throw new MappingFailure("元項目と列の型が一致しません。");
    }

    private object FromLiteral(string column, JsonElement literal)
    {
        try
        {
            if (IsColumn(column, "Num"))
            {
                return literal.GetDecimal();
            }

            if (IsColumn(column, "Check"))
            {
                return literal.GetBoolean();
            }

            var text = literal.GetString() ?? throw new MappingFailure("値が文字列ではありません。");
            if (IsColumn(column, "Date"))
            {
                return FormatDate(DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None));
            }

            return text;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new MappingFailure("固定値・既定値の型が列と一致しません。");
        }
    }

    private string FormatDate(DateTimeOffset value)
        => TimeZoneInfo.ConvertTime(value, _timeZone).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool IsColumn(string column, params string[] kinds)
        => kinds.Any(kind => column.StartsWith(kind, StringComparison.Ordinal));

    private static string ComputeVersion(IReadOnlyList<MappingConfiguration> rules, string externalKeyColumn, string zone)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("externalKeyColumn", externalKeyColumn);
            writer.WriteString("timeZone", zone);
            writer.WriteStartArray("rules");
            foreach (var rule in rules)
            {
                writer.WriteStartObject();
                writer.WriteString("target", rule.Target);
                writer.WriteString("source", rule.Source);
                writer.WritePropertyName("constant");
                WriteLiteral(writer, rule.Constant);
                writer.WritePropertyName("default");
                WriteLiteral(writer, rule.Default);
                writer.WriteString("transform", rule.Transform.ToString());
                writer.WriteString("separator", rule.Separator);
                writer.WriteString("onMissing", rule.OnMissing.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteLiteral(Utf8JsonWriter writer, JsonElement? literal)
    {
        if (literal is { } element)
        {
            element.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private sealed class MappingFailure(string message) : Exception(message);
}
