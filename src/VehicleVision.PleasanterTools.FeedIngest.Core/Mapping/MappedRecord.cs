using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Mapping;

/// <summary>対応づけできなかった理由。記事の本文・値を含めない。</summary>
/// <param name="Target">対象の列名。</param>
/// <param name="Reason">理由。</param>
public sealed record MappingError(string Target, string Reason);

/// <summary>1 記事の対応づけの結果。エラーがあれば記事全体を保留し、<see cref="Record"/> は <c>null</c>。</summary>
public sealed record MappingResult(MappedRecord? Record, IReadOnlyList<MappingError> Errors)
{
    public bool IsSuccess => Record is not null;
}

/// <summary>
/// Pleasanter へ送る列値。値は文字列・<see cref="decimal"/>・<see cref="bool"/> のいずれか。
/// </summary>
public sealed class MappedRecord
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly SortedDictionary<string, object> _columns;

    public MappedRecord(string externalKeyColumn, string externalKey, IEnumerable<KeyValuePair<string, object>> columns)
    {
        ExternalKeyColumn = externalKeyColumn;
        ExternalKey = externalKey;
        _columns = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in columns)
        {
            _columns.Add(name, value);
        }
    }

    public string ExternalKeyColumn { get; }

    public string ExternalKey { get; }

    /// <summary>設定が所有する列（外部キー列を除く）。列名の序数順。</summary>
    public IReadOnlyDictionary<string, object> Columns => _columns;

    /// <summary>
    /// 変更検出用のハッシュ。所有列の値だけから決定的に計算する（現在時刻・乱数を含めない）。
    /// 同じ外部キーで値と設定版が同じなら送らない、の判定に使う。
    /// </summary>
    public string ValueHash
    {
        get
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                writer.WriteStartObject();
                foreach (var (name, value) in _columns)
                {
                    WriteValue(writer, name, value);
                }

                writer.WriteEndObject();
            }

            return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        }
    }

    /// <summary>作成の本文。所有列と外部キー列を同じ要求で保存する。</summary>
    public IReadOnlyDictionary<string, object?> ToCreatePayload() => Build(includeExternalKey: true);

    /// <summary>更新の本文。所有列だけを送る（外部キー・未対応列・利用者の他列には触れない）。</summary>
    public IReadOnlyDictionary<string, object?> ToUpdatePayload() => Build(includeExternalKey: false);

    private Dictionary<string, object?> Build(bool includeExternalKey)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in _columns)
        {
            Place(payload, name, value);
        }

        if (includeExternalKey)
        {
            Place(payload, ExternalKeyColumn, ExternalKey);
        }

        return payload;
    }

    private static void Place(Dictionary<string, object?> payload, string column, object value)
    {
        if (column is "Title" or "Body")
        {
            payload[column] = value;
            return;
        }

        var hash = HashName(column);
        if (!payload.TryGetValue(hash, out var existing))
        {
            existing = new Dictionary<string, object?>(StringComparer.Ordinal);
            payload[hash] = existing;
        }

        ((Dictionary<string, object?>)existing!)[column] = value;
    }

    /// <summary>列名から、API の入れ物の名前（ClassHash 等）を返す。</summary>
    internal static string HashName(string column)
    {
        foreach (var kind in new[] { "Class", "Date", "Description", "Num", "Check" })
        {
            if (column.StartsWith(kind, StringComparison.Ordinal))
            {
                return kind + "Hash";
            }
        }

        throw new ArgumentException("未対応の列です。", nameof(column));
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, object value)
    {
        switch (value)
        {
            case string text:
                writer.WriteString(name, text);
                break;
            case decimal number:
                // 1.0 と 1 を同じ値として扱う
                writer.WriteNumber(name, number / 1.000000000000000000000000000000000m);
                break;
            case bool flag:
                writer.WriteBoolean(name, flag);
                break;
            default:
                throw new InvalidOperationException("未対応の値の型です。");
        }
    }
}
