using System.Text.Json;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

public sealed record FeedIngestConfiguration(
    int SchemaVersion, DestinationConfiguration Pleasanter, IReadOnlyList<SourceConfiguration> Sources);

public sealed record DestinationConfiguration(
    string Id, Uri BaseUrl, decimal ApiVersion, SecretReference ApiKey, string ApiKeyUserTimeZoneId);

public enum FeedFormat { Auto, Rss20, Atom10 }
public enum TargetMode { Upsert, CreateOnly }
public enum AuthenticationKind { None, Basic, Headers }
public enum MappingTransform { Identity, Join, Json }
public enum MissingValueBehavior { Omit, Error }

public sealed record SourceConfiguration(
    string Id, Uri Url, FeedFormat Format, HttpConfiguration Http,
    TargetConfiguration Target, IReadOnlyList<MappingConfiguration> Mapping);

public sealed record HttpConfiguration(
    int TimeoutSeconds, int MaxRedirects, int MaxBodyBytes, AuthenticationConfiguration Authentication);

public sealed record AuthenticationConfiguration(
    AuthenticationKind Type, SecretReference? Username, SecretReference? Password,
    IReadOnlyList<HeaderConfiguration> Headers);

public sealed record HeaderConfiguration(string Name, SecretReference Value);

public sealed record TargetConfiguration(long SiteId, string ExternalKeyColumn, TargetMode Mode);

/// <summary>定数と既定値は JSON の型を保持する。null は未指定を表す。</summary>
public sealed record MappingConfiguration(
    string Target, string? Source, JsonElement? Constant, JsonElement? Default,
    MappingTransform Transform, string? Separator, MissingValueBehavior OnMissing);

public sealed class ConfigurationException(int lineNumber, string key, string reason)
    : InvalidOperationException($"設定エラー: 行 {lineNumber}, キー {key}: {reason}")
{
    public int LineNumber { get; } = lineNumber;
    public string Key { get; } = key;
}
