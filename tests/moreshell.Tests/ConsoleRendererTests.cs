using moreshell.Render;
using moreshell.Vt;

namespace moreshell.Tests;

/// <summary>
/// 渲染层测试。
///
/// 最要紧的是第一张表：ANSI 调色板序号与 <see cref="ConsoleColor"/> 的枚举值**不是一回事**
/// （ANSI 1=红，而 ConsoleColor 1=DarkBlue）。这类错位不会崩，只会让所有颜色悄悄错位，
/// 人眼在浅色背景下很难第一时间发现，所以必须用断言钉死。
/// </summary>
public class ConsoleRendererTests
{
    [Theory]
    [InlineData(0, ConsoleColor.Black)]
    [InlineData(1, ConsoleColor.DarkRed)]
    [InlineData(2, ConsoleColor.DarkGreen)]
    [InlineData(3, ConsoleColor.DarkYellow)]
    [InlineData(4, ConsoleColor.DarkBlue)]
    [InlineData(5, ConsoleColor.DarkMagenta)]
    [InlineData(6, ConsoleColor.DarkCyan)]
    [InlineData(7, ConsoleColor.Gray)]
    [InlineData(8, ConsoleColor.DarkGray)]
    [InlineData(9, ConsoleColor.Red)]
    [InlineData(10, ConsoleColor.Green)]
    [InlineData(11, ConsoleColor.Yellow)]
    [InlineData(12, ConsoleColor.Blue)]
    [InlineData(13, ConsoleColor.Magenta)]
    [InlineData(14, ConsoleColor.Cyan)]
    [InlineData(15, ConsoleColor.White)]
    public void AnsiPaletteIndex_MapsToConsoleColor(int ansi, ConsoleColor expected)
        => Assert.Equal(expected, ConsoleRenderer.ToConsole(ansi, isBackground: false));

    /// <summary>ANSI 1（红）绝不能映射成 ConsoleColor.DarkBlue——枚举数值恰好也是 1，是最容易踩的坑。</summary>
    [Fact]
    public void AnsiRed_IsNotDarkBlue()
        => Assert.Equal(ConsoleColor.DarkRed, ConsoleRenderer.ToConsole(1, isBackground: false));

    [Fact]
    public void DefaultColor_GivesTerminalDefaults()
    {
        Assert.Equal(ConsoleColor.Gray, ConsoleRenderer.ToConsole(Colors.Default, isBackground: false));
        Assert.Equal(ConsoleColor.Black, ConsoleRenderer.ToConsole(Colors.Default, isBackground: true));
    }

    // ---------- 样式标志 ----------

    [Fact]
    public void Bold_PromotesLowPaletteToBright()
    {
        var style = Style.Default.WithFg(1).WithFlag(StyleFlags.Bold, true);
        var (fg, _) = ConsoleRenderer.ResolveColors(style);

        Assert.Equal(ConsoleColor.Red, fg);          // 1 -> 9
    }

    [Fact]
    public void Bold_OnAlreadyBrightColor_ChangesNothing()
    {
        var style = Style.Default.WithFg(9).WithFlag(StyleFlags.Bold, true);
        var (fg, _) = ConsoleRenderer.ResolveColors(style);

        Assert.Equal(ConsoleColor.Red, fg);
    }

    [Fact]
    public void Inverse_SwapsFgAndBg()
    {
        var style = Style.Default.WithFg(1).WithBg(4).WithFlag(StyleFlags.Inverse, true);
        var (fg, bg) = ConsoleRenderer.ResolveColors(style);

        Assert.Equal(ConsoleColor.DarkBlue, fg);
        Assert.Equal(ConsoleColor.DarkRed, bg);
    }

    [Fact]
    public void Hidden_MakesTextSameColorAsBackground()
    {
        var style = Style.Default.WithFg(1).WithBg(2).WithFlag(StyleFlags.Hidden, true);
        var (fg, bg) = ConsoleRenderer.ResolveColors(style);

        Assert.Equal(bg, fg);
        Assert.Equal(ConsoleColor.DarkGreen, fg);
    }

    // ---------- 256 色 / 真彩色降采样 ----------

    [Theory]
    [InlineData(0x1000000 | (255 << 16) | (0 << 8) | 0, ConsoleColor.Red)]
    [InlineData(0x1000000 | (0 << 16) | (0 << 8) | 255, ConsoleColor.Blue)]
    [InlineData(0x1000000 | (255 << 16) | (255 << 8) | 0, ConsoleColor.Yellow)]
    [InlineData(0x1000000 | (0 << 16) | (255 << 8) | 0, ConsoleColor.Green)]
    [InlineData(0x1000000 | (255 << 16) | (255 << 8) | 255, ConsoleColor.White)]
    [InlineData(0x1000000, ConsoleColor.Black)]
    public void TrueColor_ResolvesToNearestOf16(int rgb, ConsoleColor expected)
        => Assert.Equal(expected, ConsoleRenderer.ToConsole(rgb, isBackground: false));

    [Theory]
    [InlineData(196, ConsoleColor.Red)]        // 6x6x6 立方里的纯红
    [InlineData(226, ConsoleColor.Yellow)]     // 255,255,95 -> 黄
    [InlineData(232, ConsoleColor.Black)]      // 灰阶最暗
    [InlineData(255, ConsoleColor.White)]      // 灰阶最亮
    public void Palette256_ResolvesToNearestOf16(int index, ConsoleColor expected)
        => Assert.Equal(expected, ConsoleRenderer.ToConsole(index, isBackground: false));

    // ---------- 分段 ----------

    [Fact]
    public void Segment_MergesConsecutiveSameStyle()
    {
        var doc = new TerminalDocument();
        doc.Put("ab");
        doc.CurrentStyle = Style.Default.WithFg(1);
        doc.Put("cd");

        var segs = ConsoleRenderer.Segment(Assert.Single(doc.Flush()));

        Assert.Equal(2, segs.Count);
        Assert.Equal("ab", segs[0].Text);
        Assert.Equal(ConsoleColor.Gray, segs[0].Fg);
        Assert.Equal("cd", segs[1].Text);
        Assert.Equal(ConsoleColor.DarkRed, segs[1].Fg);
    }

    [Fact]
    public void Segment_UniformLine_YieldsOneSegment()
    {
        var doc = new TerminalDocument();
        doc.Put("abc");

        var segs = ConsoleRenderer.Segment(Assert.Single(doc.Flush()));

        Assert.Equal("abc", Assert.Single(segs).Text);
    }

    [Fact]
    public void Segment_CarriesFlagsThrough()
    {
        var doc = new TerminalDocument();
        doc.CurrentStyle = Style.Default.WithFg(1).WithFlag(StyleFlags.Bold, true);
        doc.Put("x");

        var seg = Assert.Single(ConsoleRenderer.Segment(Assert.Single(doc.Flush())));
        Assert.Equal(StyleFlags.Bold, seg.Flags);
    }

    /// <summary>把一段彩色文本切分后，各段拼起来必须还原原文——这是渲染不丢字的必要条件。</summary>
    [Fact]
    public void Segment_Concatenation_RestoresOriginalText()
    {
        var doc = new TerminalDocument();
        doc.Put("中文");
        doc.CurrentStyle = Style.Default.WithFg(2);
        doc.Put("-ABC");
        doc.CurrentStyle = Style.Default;
        doc.Put("结尾");

        var segs = ConsoleRenderer.Segment(Assert.Single(doc.Flush()));

        Assert.Equal("中文-ABC结尾", string.Concat(segs.Select(s => s.Text)));
    }
}
