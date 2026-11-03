using System.Text;
using moreshell.Runner;

namespace moreshell.Tests;

/// <summary>
/// 编码嗅探器测试。
///
/// 这是整个原型里最容易"看起来对、实际乱码"的一块，所以断言全部基于**字节**，
/// 不依赖任何控制台显示。核心不变量：
///   1. 非法 UTF-8 字节 → 立刻判定为兜底编码；
///   2. 足够长且合法的 UTF-8（含多字节）→ 判定为 UTF-8；
///   3. 纯 ASCII → 不判定，直接放行（零延迟）；
///   4. 判定一次定终身，之后不再翻案。
/// </summary>
public class OutputSnifferTests
{
    public OutputSnifferTests() => Encodings.EnsureRegistered();

    /// <summary>兜底编码：中文 Windows 上是 GBK/936，Linux 上 OEM 无法表示中文，故回退 UTF-8。</summary>
    private static Encoding Fallback =>
        OperatingSystem.IsWindows() ? Encodings.Oem : Encoding.UTF8;

    /// <summary>一定会被判为"非 UTF-8"的字节：0xFF 不是任何合法 UTF-8 序列的首字节。</summary>
    private static readonly byte[] DefinitelyNotUtf8 = { 0x4E, 0x2D, 0xFF, 0xFE, 0x41 };

    private static (OutputSniffer Sniffer, StringBuilder Out) New()
        => (new OutputSniffer(Fallback), new StringBuilder());

    [Fact]
    public void InvalidUtf8Byte_IsRejectedImmediately()
    {
        var (s, outp) = New();
        s.Push(DefinitelyNotUtf8, DefinitelyNotUtf8.Length, text => outp.Append(text));

        Assert.True(s.IsDecided);
        Assert.Equal(Fallback.WebName, s.Chosen!.WebName);
    }

    [Fact]
    public void OemEncodedChinese_RoundTrips()
    {
        const string text = "中文测试-ABC";
        var bytes = Fallback.GetBytes(text);

        var (s, outp) = New();
        s.Push(bytes, bytes.Length, text => outp.Append(text));
        s.Finish(text => outp.Append(text));

        Assert.Equal(text, outp.ToString());
    }

    /// <summary>足够长的合法 UTF-8（且含多字节）才会被判成 UTF-8；短的先攒着。</summary>
    [Fact]
    public void LongValidUtf8_IsDecidedAsUtf8()
    {
        var text = string.Concat(Enumerable.Repeat("中文", 40));   // 240 字节
        var bytes = Encoding.UTF8.GetBytes(text);

        var (s, outp) = New();
        s.Push(bytes, bytes.Length, text => outp.Append(text));

        Assert.True(s.IsDecided);
        Assert.Equal(Encoding.UTF8.WebName, s.Chosen!.WebName);
        Assert.Equal(text, outp.ToString());
    }

    [Fact]
    public void ShortValidUtf8_IsHeldUntilDecided()
    {
        var bytes = Encoding.UTF8.GetBytes("中文");                 // 仅 6 字节

        var (s, outp) = New();
        s.Push(bytes, bytes.Length, text => outp.Append(text));

        Assert.False(s.IsDecided);       // 未满置信长度，先攒着
        Assert.Equal(string.Empty, outp.ToString());

        s.Finish(text => outp.Append(text));           // 进程结束时才定案
        Assert.Equal("中文", outp.ToString());
    }

    [Fact]
    public void PureAscii_IsEmittedWithoutDeciding()
    {
        var bytes = Encoding.ASCII.GetBytes("plain ascii output");

        var (s, outp) = New();
        s.Push(bytes, bytes.Length, text => outp.Append(text));

        Assert.False(s.IsDecided);       // ASCII 两种编码等价，没必要判定
        Assert.Equal("plain ascii output", outp.ToString());
    }

    /// <summary>多字节字符被拆在两次读取之间——这是管道分块的常态。</summary>
    [Fact]
    public void MultibyteCharacterSplitAcrossPushes_DecodesCorrectly()
    {
        var bytes = Encoding.UTF8.GetBytes("中文");

        var (s, outp) = New();
        s.Push(new[] { bytes[0] }, 1, text => outp.Append(text));        // 只到第一个字的首字节
        Assert.False(s.IsDecided);
        Assert.Equal(string.Empty, outp.ToString());

        s.Push(new[] { bytes[1], bytes[2], bytes[3], bytes[4], bytes[5] }, 5, text => outp.Append(text));
        s.Finish(text => outp.Append(text));

        Assert.Equal("中文", outp.ToString());
        Assert.Equal(Encoding.UTF8.WebName, s.Chosen!.WebName);
    }

    /// <summary>转义序列恰好横跨两次读取，判定不能把它切碎。</summary>
    [Fact]
    public void EscapeSequenceSplitAcrossPushes_StillEmits()
    {
        var all = Encoding.UTF8.GetBytes("\x1b[31m红色\x1b[0m");

        var (s, outp) = New();
        for (int i = 0; i < all.Length; i++)
            s.Push(new[] { all[i] }, 1, text => outp.Append(text));
        s.Finish(text => outp.Append(text));

        Assert.Equal("\x1b[31m红色\x1b[0m", outp.ToString());
    }

    [Fact]
    public void EmptyInput_FallsBackOnFinish()
    {
        var (s, outp) = New();
        s.Finish(text => outp.Append(text));

        Assert.True(s.IsDecided);
        Assert.Equal(Fallback.WebName, s.Chosen!.WebName);
        Assert.Equal(string.Empty, outp.ToString());
    }

    /// <summary>判定不可逆：定成 UTF-8 后再来非法字节也不改判（否则文本会前后不一致）。</summary>
    [Fact]
    public void VerdictIsSticky_EvenWhenLaterBytesAreInvalid()
    {
        var (s, outp) = New();
        var longUtf8 = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("中文", 40)));
        s.Push(longUtf8, longUtf8.Length, _ => { });

        Assert.True(s.IsDecided);
        Assert.Equal(Encoding.UTF8.WebName, s.Chosen!.WebName);

        s.Push(DefinitelyNotUtf8, DefinitelyNotUtf8.Length, text => outp.Append(text));

        Assert.Equal(Encoding.UTF8.WebName, s.Chosen!.WebName);   // 不翻案
    }

    [Fact]
    public void OverlongEncoding_IsRejected()
    {
        // C0 AF 是 "/" 的 overlong 编码，合法 UTF-8 里不允许出现
        var bytes = new byte[] { 0x41, 0xC0, 0xAF, 0x42 };
        var (s, outp) = New();
        s.Push(bytes, bytes.Length, text => outp.Append(text));

        Assert.Equal(Fallback.WebName, s.Chosen!.WebName);
    }

    [Fact]
    public void Utf16SurrogateRange_IsRejected()
    {
        // ED A0 80 = U+D800，UTF-8 里不允许编码代理区
        var bytes = new byte[] { 0x41, 0xED, 0xA0, 0x80, 0x42 };
        var (s, _) = New();
        s.Push(bytes, bytes.Length, _ => { });

        Assert.Equal(Fallback.WebName, s.Chosen!.WebName);
    }
}
