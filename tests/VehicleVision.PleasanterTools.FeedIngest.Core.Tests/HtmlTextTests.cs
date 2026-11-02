namespace VehicleVision.PleasanterTools.FeedIngest.Core.Tests;

public sealed class HtmlTextTests
{
    [Fact]
    public void nullはnullを返す()
    {
        Assert.Null(HtmlText.ToPlainText(null));
    }

    [Theory]
    [InlineData("<p>a</p><p>b</p>", "a\n\nb")]
    [InlineData("a<br>b<br/>c", "a\nb\nc")]
    [InlineData("<ul><li>x</li><li>y</li></ul>", "x\ny")]
    [InlineData("<b>太</b><i>字</i>", "太字")]
    [InlineData("a &amp; b &lt;c&gt; &#12354;", "a & b <c> あ")]
    [InlineData("  a \t  b  \n\n\n\n c ", "a b\n\nc")]
    [InlineData("1 < 2 and 3 > 2", "1 < 2 and 3 > 2")]
    public void タグを除きブロックは改行にして空白を畳む(string html, string expected)
    {
        Assert.Equal(expected, HtmlText.ToPlainText(html));
    }

    [Theory]
    [InlineData("a<script>alert('</p>')</script>b", "ab")]
    [InlineData("a<SCRIPT type=x>if (a<b) {}</SCRIPT>b", "ab")]
    [InlineData("a<style>p{color:red}</style>b", "ab")]
    [InlineData("a<script>never closed", "a")]
    [InlineData("a<!-- <b>hidden</b> -->b", "ab")]
    [InlineData("a<img src=\"x>y\" onerror=\"z()\">b", "ab")]
    [InlineData("<a href=\"javascript:x()\">リンク</a>", "リンク")]
    public void スクリプトとコメントと属性を出力しない(string html, string expected)
    {
        Assert.Equal(expected, HtmlText.ToPlainText(html));
    }

    [Fact]
    public void エスケープされたタグは文字のまま残す()
    {
        Assert.Equal("<script>x</script>", HtmlText.ToPlainText("&lt;script&gt;x&lt;/script&gt;"));
    }

    [Fact]
    public void 制御文字を除く()
    {
        Assert.Equal("ab", HtmlText.ToPlainText("a\u0000\u0007b"));
    }
}
