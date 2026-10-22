using System.Text;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

/// <summary>秘密値を保持せず、環境変数または秘密ファイルから呼出時に解決する。</summary>
public sealed partial class SecretReference
{
    public const int MaxFileBytes = 65536;

    public SecretReference(string environmentVariable)
    {
        if (environmentVariable is null || !EnvironmentName().IsMatch(environmentVariable)
            || environmentVariable.EndsWith("_FILE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("秘密参照は _FILE を含まない環境変数名が必要です。");
        }
        EnvironmentVariable = environmentVariable;
    }

    public string EnvironmentVariable { get; }

    public string Resolve(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var value = environment(EnvironmentVariable);
        var file = environment(EnvironmentVariable + "_FILE");
        if (value is not null && file is not null)
        {
            throw Error("環境変数と _FILE が競合しています。");
        }
        if (file is not null)
        {
            if (!Path.IsPathFullyQualified(file))
            {
                throw Error("_FILE は絶対パスが必要です。");
            }
            try
            {
                using var stream = File.OpenRead(file);
                var bytes = new byte[MaxFileBytes + 1];
                var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                if (count > MaxFileBytes)
                {
                    throw Error("秘密ファイルは 64 KiB 以下が必要です。");
                }
                var start = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                value = new UTF8Encoding(false, true).GetString(bytes, start, count - start);
                if (value.EndsWith("\r\n", StringComparison.Ordinal))
                {
                    value = value[..^2];
                }
                else if (value.EndsWith('\n') || value.EndsWith('\r'))
                {
                    value = value[..^1];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or DecoderFallbackException or ArgumentException or NotSupportedException
                or System.Security.SecurityException)
            {
                throw Error("秘密ファイルを UTF-8 として読み取れません。");
            }
        }
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
        {
            throw Error("秘密が不在・空、または CR/LF を含みます。");
        }
        return value;
    }

    private InvalidOperationException Error(string reason) =>
        new($"秘密参照 {EnvironmentVariable}: {reason}");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentName();
}
