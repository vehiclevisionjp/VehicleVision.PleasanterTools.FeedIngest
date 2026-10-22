using System.Text;
using VehicleVision.PleasanterTools.FeedIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.FeedIngest.Core.Tests;

public sealed class SecretReferenceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("secret\r\ninjection")]
    [InlineData("secret\ninjection")]
    public void 不在と不正値は秘密を含まない例外にする(string? value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new SecretReference("TEST_KEY").Resolve(name => name == "TEST_KEY" ? value : null));
        Assert.Contains("TEST_KEY", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public void 直接値は空白を勝手に除去しない()
    {
        Assert.Equal(" secret ", new SecretReference("TEST_KEY").Resolve(name => name == "TEST_KEY" ? " secret " : null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret")]
    public void 空の環境変数もFILEと競合する(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SecretReference("TEST_KEY").Resolve(name => name == "TEST_KEY" ? value : "absolute-file"));
    }

    [Theory]
    [InlineData("secret", "secret")]
    [InlineData("secret\n", "secret")]
    [InlineData("secret\r\n", "secret")]
    [InlineData("secret\r", "secret")]
    [InlineData(" secret \n", " secret ")]
    public void 秘密ファイルは末尾の改行一組のみ除去する(string content, string expected)
    {
        WithFile(Encoding.UTF8.GetBytes(content), file =>
            Assert.Equal(expected, ResolveFile(file)));
    }

    [Theory]
    [InlineData(65535, true)]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public void ファイルサイズ上限の前後を検査する(int size, bool valid)
    {
        WithFile(Enumerable.Repeat((byte)'x', size).ToArray(), file =>
        {
            if (valid)
            {
                Assert.Equal(size, ResolveFile(file).Length);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => ResolveFile(file));
            }
        });
    }

    [Fact]
    public void 不正UTF8と二重改行と空ファイルを拒否する()
    {
        foreach (var bytes in new byte[][] { [0xff], [], Encoding.UTF8.GetBytes("secret\n\n") })
        {
            WithFile(bytes, file =>
            {
                var error = Assert.Throws<InvalidOperationException>(() => ResolveFile(file));
                Assert.DoesNotContain(file, error.ToString());
                Assert.DoesNotContain("secret", error.ToString());
            });
        }
    }

    [Fact]
    public void 相対パスと不在ファイルを拒否しパスを漏らさない()
    {
        foreach (var file in new[] { "relative-secret", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) })
        {
            var error = Assert.Throws<InvalidOperationException>(() => ResolveFile(file));
            Assert.DoesNotContain(file, error.ToString());
        }
    }

    private static string ResolveFile(string file) =>
        new SecretReference("TEST_KEY").Resolve(name => name == "TEST_KEY_FILE" ? file : null);

    private static void WithFile(byte[] bytes, Action<string> action)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, bytes);
            action(file);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
