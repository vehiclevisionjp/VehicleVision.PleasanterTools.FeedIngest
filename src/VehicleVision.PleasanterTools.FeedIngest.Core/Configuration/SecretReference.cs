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
            throw new InvalidOperationException("A secret reference requires an environment variable name that does not end with _FILE.");
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
            throw Error("The environment variable and its _FILE variant are both set. Set only one.");
        }
        if (file is not null)
        {
            if (!Path.IsPathFullyQualified(file))
            {
                throw Error("The _FILE variable requires an absolute path.");
            }
            try
            {
                using var stream = File.OpenRead(file);
                var bytes = new byte[MaxFileBytes + 1];
                var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                if (count > MaxFileBytes)
                {
                    throw Error("The file must be 64 KiB or smaller.");
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
                throw Error("The file cannot be read as UTF-8.");
            }
        }
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
        {
            throw Error("The value is not set, is empty, or contains CR or LF.");
        }
        return value;
    }

    private InvalidOperationException Error(string reason) =>
        new($"Secret reference {EnvironmentVariable}: {reason}");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentName();
}
