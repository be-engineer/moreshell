using moreshell.Vt;

namespace moreshell.Tests;

/// <summary>
/// SGR 解析测试。SGR 的坑几乎全在"参数怎么切分"上，
/// 这里的每一条都对应一种真实程序会发出的写法。
/// </summary>
public class SgrTests
{
    private static Style Apply(params int[] p) => Sgr.Apply(Style.Default, p);

    [Fact]
    public void Empty_ResetsToDefault()
    {
        var start = Style.Default.WithFg(3).WithFlag(StyleFlags.Bold, true);
        Assert.Equal(Style.Default, Sgr.Apply(start, Array.Empty<int>()));
    }

    [Fact]
    public void Zero_ResetsToDefault()
    {
        var start = Style.Default.WithFg(3).WithFlag(StyleFlags.Bold, true);
        Assert.Equal(Style.Default, Apply(0));
        Assert.Equal(Style.Default, Sgr.Apply(start, new[] { 0 }));
    }

    [Theory]
    [InlineData(1, StyleFlags.Bold)]
    [InlineData(2, StyleFlags.Dim)]
    [InlineData(3, StyleFlags.Italic)]
    [InlineData(4, StyleFlags.Underline)]
    [InlineData(5, StyleFlags.Blink)]
    [InlineData(7, StyleFlags.Inverse)]
    [InlineData(8, StyleFlags.Hidden)]
    [InlineData(9, StyleFlags.Strike)]
    public void AttributeFlags_Set(int code, StyleFlags flag)
        => Assert.True(Apply(code).Has(flag));

    [Theory]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(32, 2)]
    [InlineData(33, 3)]
    [InlineData(34, 4)]
    [InlineData(35, 5)]
    [InlineData(36, 6)]
    [InlineData(37, 7)]
    public void Foreground_30To37(int code, int expected)
        => Assert.Equal(expected, Apply(code).Fg);

    [Theory]
    [InlineData(40, 0)]
    [InlineData(44, 4)]
    [InlineData(47, 7)]
    public void Background_40To47(int code, int expected)
        => Assert.Equal(expected, Apply(code).Bg);

    [Theory]
    [InlineData(90, 8)]
    [InlineData(93, 11)]
    [InlineData(97, 15)]
    public void BrightForeground_90To97(int code, int expected)
        => Assert.Equal(expected, Apply(code).Fg);

    [Theory]
    [InlineData(100, 8)]
    [InlineData(104, 12)]
    public void BrightBackground_100To107(int code, int expected)
        => Assert.Equal(expected, Apply(code).Bg);

    [Fact]
    public void ResetForeground39_KeepsBackground()
    {
        var s = Apply(41, 31, 39);
        Assert.Equal(Colors.Default, s.Fg);
        Assert.Equal(1, s.Bg);
    }

    [Fact]
    public void Combined_BoldAndRed()
    {
        var s = Apply(1, 31);
        Assert.True(s.Has(StyleFlags.Bold));
        Assert.Equal(1, s.Fg);
    }

    /// <summary>22 只关粗体/暗淡，不能顺手把颜色也清了。</summary>
    [Fact]
    public void TwentyTwo_ClearsBoldButKeepsColor()
    {
        var s = Apply(1, 2, 31, 22);
        Assert.False(s.Has(StyleFlags.Bold));
        Assert.False(s.Has(StyleFlags.Dim));
        Assert.Equal(1, s.Fg);
    }

    [Theory]
    [InlineData(23, StyleFlags.Italic)]
    [InlineData(24, StyleFlags.Underline)]
    [InlineData(25, StyleFlags.Blink)]
    [InlineData(27, StyleFlags.Inverse)]
    [InlineData(28, StyleFlags.Hidden)]
    [InlineData(29, StyleFlags.Strike)]
    public void ResetCodes_ClearOnlyTheirOwnFlag(int code, StyleFlags flag)
    {
        var start = Style.Default
            .WithFlag(StyleFlags.Bold, true)
            .WithFlag(flag, true)
            .WithFg(2);
        var s = Sgr.Apply(start, new[] { code });

        Assert.False(s.Has(flag));
        Assert.True(s.Has(StyleFlags.Bold));
        Assert.Equal(2, s.Fg);
    }

    // ---------- 扩展色 ----------

    [Theory]
    [InlineData(196)]
    [InlineData(21)]
    [InlineData(255)]
    public void TrueColor256_Semicolon(int index)
        => Assert.Equal(index, Apply(38, 5, index).Fg);

    [Theory]
    [InlineData(196)]
    [InlineData(226)]
    public void TrueColor256_Colon(int index)
    {
        var p = ColonParams(38, 5, index);
        Assert.Equal(index, Sgr.Apply(Style.Default, p).Fg);
    }

    [Fact]
    public void TrueColor_Semicolon()
    {
        var s = Apply(38, 2, 0, 255, 128);
        Assert.Equal(Colors.Rgb(0, 255, 128), s.Fg);
    }

    /// <summary>冒号形态 + colorSpaceId（38:2:0:255:0:1）：第 3 项是色域，真正的 R 从第 4 项开始。</summary>
    [Fact]
    public void TrueColor_Colon_WithColorSpaceId_SkipsColorSpace()
    {
        var p = ColonParams(38, 2, 0, 255, 0, 1);
        Assert.Equal(Colors.Rgb(255, 0, 1), Sgr.Apply(Style.Default, p).Fg);
    }

    /// <summary>冒号形态但只有 5 项（38:2:255:0:0）：第 3 项就是 R，不能再往后挪一位。</summary>
    [Fact]
    public void TrueColor_Colon_WithoutColorSpaceId_UsesDirectRgb()
    {
        var p = ColonParams(38, 2, 255, 0, 0);
        Assert.Equal(Colors.Rgb(255, 0, 0), Sgr.Apply(Style.Default, p).Fg);
    }

    /// <summary>分号形态后面还跟着别的 SGR（38;2;r;g;b;1）：末位的 1 是粗体，绝不能被当成 colorSpaceId 吃掉。</summary>
    [Fact]
    public void TrueColor_Semicolon_TrailingParamIsNotColorSpace()
    {
        var s = Apply(38, 2, 255, 0, 0, 1);
        Assert.Equal(Colors.Rgb(255, 0, 0), s.Fg);
        Assert.True(s.Has(StyleFlags.Bold));
    }

    [Fact]
    public void BackgroundTrueColor_Semicolon()
        => Assert.Equal(Colors.Rgb(1, 2, 3), Apply(48, 2, 1, 2, 3).Bg);

    /// <summary>被截断的 38（参数缺失）不能崩，也不能乱改颜色。</summary>
    [Theory]
    [InlineData(38)]
    [InlineData(38, 5)]
    [InlineData(38, 2)]
    [InlineData(38, 2, 255)]
    public void TruncatedExtendedColor_IsIgnoredSafely(params int[] p)
        => Assert.Equal(Colors.Default, Apply(p).Fg);

    [Fact]
    public void UnknownCode_IsIgnored()
    {
        var start = Style.Default.WithFg(5);
        Assert.Equal(start, Sgr.Apply(start, new[] { 999 }));
    }

    /// <summary>把参数构造成"冒号分隔"的样子——模拟解析器产出的带 ColonSeparator 位的数组。</summary>
    private static int[] ColonParams(params int[] values)
    {
        var p = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
            p[i] = values[i] | (i == values.Length - 1 ? 0 : VtAction.ColonSeparator);
        return p;
    }
}
