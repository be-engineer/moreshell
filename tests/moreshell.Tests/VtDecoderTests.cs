using moreshell.Vt;

namespace moreshell.Tests;

/// <summary>
/// 转义序列状态机的测试。
///
/// 重点覆盖两类最危险的情况：
///   1. 序列被拆分在多次读取之间（管道分块时必然发生，是"偶发乱码"的头号来源）；
///   2. SGR 的五种写法，尤其是带 colorSpaceId 的冒号形式与分号形式的区别。
/// </summary>
public class VtDecoderTests
{
    private static List<VtAction> Decode(string text, int chunkSize = 0)
    {
        var actions = new List<VtAction>();
        var d = new VtDecoder();
        if (chunkSize <= 0)
        {
            d.Decode(text, actions.Add);
            return actions;
        }

        for (int i = 0; i < text.Length; i += chunkSize)
            d.Decode(text.Substring(i, Math.Min(chunkSize, text.Length - i)), actions.Add);
        return actions;
    }

    // ---------- 纯文本 ----------

    [Fact]
    public void PlainText_IsASinglePrint()
    {
        var a = Assert.Single(Decode("hello"));
        Assert.Equal(VtActionKind.Print, a.Kind);
        Assert.Equal("hello", a.Text);
    }

    [Fact]
    public void Chinese_StaysInOnePrint()
    {
        var a = Assert.Single(Decode("中文-ABC"));
        Assert.Equal("中文-ABC", a.Text);
    }

    [Fact]
    public void ControlCharacters_AreExecuteActions()
    {
        var a = Decode("a\nb\rc\bd\te");

        Assert.Equal(
            new[] { VtActionKind.Print, VtActionKind.Execute, VtActionKind.Print,
                    VtActionKind.Execute, VtActionKind.Print, VtActionKind.Execute,
                    VtActionKind.Print, VtActionKind.Execute, VtActionKind.Print },
            a.Select(x => x.Kind));

        Assert.Equal(new[] { 10, 13, 8, 9 },
            a.Where(x => x.Kind == VtActionKind.Execute).Select(x => x.Code));
    }

    // ---------- CSI 参数形态 ----------

    [Fact]
    public void Sgr_SingleParam()
    {
        var c = Assert.Single(Decode("\x1b[31m"));
        Assert.Equal(VtActionKind.CsiDispatch, c.Kind);
        Assert.Equal('m', c.Final);
        Assert.Equal(new[] { 31 }, c.Params);
        Assert.Equal('\0', c.Private);
    }

    [Fact]
    public void Sgr_MultipleParams_Semicolon()
        => Assert.Equal(new[] { 1, 31 }, Decode("\x1b[1;31m")[0].Params);

    [Fact]
    public void Sgr_256Color_Semicolon()
        => Assert.Equal(new[] { 38, 5, 196 }, Decode("\x1b[38;5;196m")[0].Params);

    [Fact]
    public void Sgr_256Color_Colon_MarksSeparator()
    {
        var p = Decode("\x1b[38:5:196m")[0].Params!;
        Assert.Equal(new[] { 38, 5, 196 }, p.Select(VtAction.Value));
        Assert.True(VtAction.EndsWithColon(p[0]));
        Assert.True(VtAction.EndsWithColon(p[1]));
    }

    [Fact]
    public void Sgr_TrueColor_Semicolon_FiveParams()
        => Assert.Equal(new[] { 38, 2, 0, 255, 0 }, Decode("\x1b[38;2;0;255;0m")[0].Params);

    /// <summary>冒号形态带 colorSpaceId：共 6 项，第 3 项是色域而非 R。</summary>
    [Fact]
    public void Sgr_TrueColor_Colon_WithColorSpaceId_KeepsSeparatorBits()
    {
        var p = Decode("\x1b[38:2:0:255:0:1m")[0].Params!;

        Assert.Equal(6, p.Length);
        Assert.Equal(new[] { 38, 2, 0, 255, 0, 1 }, p.Select(VtAction.Value));
        Assert.True(VtAction.EndsWithColon(p[0]));   // 38:
        Assert.True(VtAction.EndsWithColon(p[1]));   // 2:
        Assert.True(VtAction.EndsWithColon(p[2]));   // 0:  <- colorSpaceId
        Assert.False(VtAction.EndsWithColon(p[5]));  // 1   <- 末项不再有冒号
    }

    [Fact]
    public void EmptyParamList_StillEmits()
    {
        var c = Assert.Single(Decode("\x1b[m"));
        Assert.Equal(VtActionKind.CsiDispatch, c.Kind);
        Assert.Empty(c.Params!);
    }

    [Fact]
    public void TrailingEmptyParam_BecomesZero()
        => Assert.Equal(new[] { 1, 0 }, Decode("\x1b[1;m")[0].Params);

    [Fact]
    public void OversizedParam_IsClamped()
        => Assert.Equal(65535, Decode("\x1b[99999m")[0].Params![0]);

    // ---------- 各类 CSI ----------

    [Theory]
    [InlineData("\x1b[0K", 'K', 0)]
    [InlineData("\x1b[1K", 'K', 1)]
    [InlineData("\x1b[2K", 'K', 2)]
    [InlineData("\x1b[2J", 'J', 2)]
    [InlineData("\x1b[5G", 'G', 5)]
    [InlineData("\x1b[10C", 'C', 10)]
    [InlineData("\x1b[3D", 'D', 3)]
    public void EraseAndMove_Series(string seq, char final, int param)
    {
        var c = Assert.Single(Decode(seq));
        Assert.Equal(final, c.Final);
        Assert.Equal(param, c.Params![0]);
    }

    [Fact]
    public void Csi_DefaultParam_AppliedByApplierNotDecoder()
    {
        var c = Assert.Single(Decode("\x1b[K"));    // 默认参数：解析器给空数组
        Assert.Empty(c.Params!);
        Assert.Equal('K', c.Final);
    }

    /// <summary>DECSET/DECRST 的私有前缀 '?' 必须被单独记下，否则会误当成 SGR 处理。</summary>
    [Fact]
    public void PrivatePrefix_IsCaptured()
    {
        var c = Assert.Single(Decode("\x1b[?25l"));
        Assert.Equal('?', c.Private);
        Assert.Equal('l', c.Final);
        Assert.Equal(new[] { 25 }, c.Params);
    }

    [Fact]
    public void UnknownFinal_StillDispatchesAndReturnsToGround()
    {
        var c = Assert.Single(Decode("\x1b[99Z"));
        Assert.Equal('Z', c.Final);

        var after = Decode("x");
        Assert.Equal("x", Assert.Single(after).Text);   // 没有卡死在序列状态
    }

    // ---------- 跨块拆分（最关键的一类） ----------

    [Fact]
    public void SequenceSplitAcrossChunks_Reassembles()
    {
        var actions = new List<VtAction>();
        var d = new VtDecoder();

        d.Decode("\x1b[3", actions.Add);
        Assert.Empty(actions);              // 序列未完成，不应有任何输出

        d.Decode("1m", actions.Add);
        var c = Assert.Single(actions);
        Assert.Equal('m', c.Final);
        Assert.Equal(new[] { 31 }, c.Params);
    }

    [Fact]
    public void SequenceSplitAtEveryByte_StillCorrect()
    {
        var a = Decode("\x1b[38;2;10;20;30m", chunkSize: 1);
        var c = Assert.Single(a);
        Assert.Equal('m', c.Final);
        Assert.Equal(new[] { 38, 2, 10, 20, 30 }, c.Params);
    }

    [Fact]
    public void TextSplitAcrossChunks_Concatenates()
    {
        var a = Decode("ab中文", chunkSize: 1);
        Assert.Equal("ab中文", string.Concat(a.Select(x => x.Text)));
    }

    [Fact]
    public void InSequence_ReflectsState()
    {
        var d = new VtDecoder();
        Assert.False(d.InSequence);

        d.Decode("\x1b", _ => { });
        Assert.True(d.InSequence);
        Assert.Equal("Escape", d.StateName);

        d.Decode("[31m", _ => { });
        Assert.False(d.InSequence);
    }

    [Fact]
    public void Reset_ClearsPendingState()
    {
        var d = new VtDecoder();
        d.Decode("\x1b[3", _ => { });
        Assert.True(d.InSequence);

        d.Reset();
        Assert.False(d.InSequence);
        Assert.Equal("Ground", d.StateName);
    }

    // ---------- OSC ----------

    [Fact]
    public void Osc_TerminatedByBel()
    {
        var a = Decode("\x1b]0;window title\x07");
        var c = Assert.Single(a);
        Assert.Equal(VtActionKind.OscDispatch, c.Kind);
        Assert.Equal(0, c.Code);
        Assert.Equal("window title", c.Text);
    }

    [Fact]
    public void Osc_TerminatedByEscBackslash()
    {
        var c = Assert.Single(Decode("\x1b]0;window title\x1b\\"));
        Assert.Equal(VtActionKind.OscDispatch, c.Kind);
        Assert.Equal(0, c.Code);
        Assert.Equal("window title", c.Text);
    }

    [Fact]
    public void Osc_DoesNotLeakIntoPrint()
    {
        var a = Decode("\x1b]0;title\a" + "after");   // 用 \a 而非 \x07：后者会和后面的 a 连读成 U+007A
        Assert.Equal(2, a.Count);
        Assert.Equal(VtActionKind.OscDispatch, a[0].Kind);
        Assert.Equal("after", a[1].Text);
    }

    /// <summary>DCS 之类不支持的字符串序列必须被吞掉，且能在 ST 处恢复同步。</summary>
    [Fact]
    public void Dcs_IsSwallowedThenRecovers()
    {
        var a = Decode("\x1bPsome dcs payload\x1b\\ok");
        Assert.Equal("ok", Assert.Single(a).Text);
    }

    [Fact]
    public void EscDispatch_KnownSequence()
    {
        var a = Decode("\x1b" + "cx");     // RIS（\x1bc 会被 C# 连读成 U+01BC，必须断开）
        Assert.Equal(VtActionKind.EscDispatch, a[0].Kind);
        Assert.Equal('c', a[0].Code);
        Assert.Equal("x", a[1].Text);
    }
}
