using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.State;

/// <summary>
/// 取り込み元の「取得の版」。URL と認証設定が変わったら変わり、保存済みの検証子（ETag／Last-Modified）を使わない根拠になる。
/// </summary>
/// <remarks>
/// 秘密の値は含めない（秘密参照の名前・ヘッダ名だけ）。マッピングの版は含めない（それは <c>EntryMapper.Version</c>）。
/// </remarks>
public static class SourceConfigVersion
{
    private static readonly JsonWriterOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    public static string Of(SourceConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            var authentication = source.Http.Authentication;
            writer.WriteStartObject();
            writer.WriteString("url", source.Url.AbsoluteUri);
            writer.WriteString("authType", authentication.Type.ToString());
            writer.WriteString("username", authentication.Username?.EnvironmentVariable);
            writer.WriteString("password", authentication.Password?.EnvironmentVariable);
            writer.WriteStartArray("headers");
            foreach (var header in authentication.Headers)
            {
                writer.WriteStartObject();
                writer.WriteString("name", header.Name);
                writer.WriteString("value", header.Value.EnvironmentVariable);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
