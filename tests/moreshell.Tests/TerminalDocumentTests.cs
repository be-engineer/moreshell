using System.Text;
using moreshell.Vt;

namespace moreshell.Tests;

/// <summary>
/// 行式终端缓冲区测试。
/// 重点是 <c>\r</c> 覆写与擦除——进度条、下载状态、转圈动画全靠它们，
/// 一旦写错表现就是"残影"和"字符叠字"。
/// </summary>
public class TerminalDocumentTests
{
    private static string LineText(StyledLine line) => string.Concat(line.View.Select(c => c.Text));

    [Fact]
    public void Put_ThenFlush_GivesOneLine()
    {
        var doc = new TerminalDocument();
        doc.Put("hello");
        var lines = doc.Flush();

        Assert.Equal("hello", LineText(Assert.Single(lines)));
    }

    [Fact]
    public void LineFeed_CommitsCurrentLine()
    {
        var doc = new TerminalDocument();
        doc.Put("a");
        doc.LineFeed();
        doc.Put("b");

        var lines = doc.Flush();
        Assert.Equal(2, lines.Count);
        Assert.Equal("a", LineText(lines[0]));
        Assert.Equal("b", LineText(lines[1]));
    }

    [Fact]
    public void Chinese_IsOneCellPerRune()
    {
        var doc = new TerminalDocument();
        doc.Put("中文");
        var line = Assert.Single(doc.Flush());

        Assert.Equal(2, line.Length);
        Assert.Equal("中", line[0]!.Value.Text);
        Assert.Equal("文", line[1]!.Value.Text);
    }

    /// <summary>代理对（emoji）必须占一个单元格，否则后续 \r 覆写会错位。</summary>
    [Fact]
    public void SurrogatePair_OccupiesOneCell()
    {
        var doc = new TerminalDocument();
        doc.Put("🙂x");
        var line = Assert.Single(doc.Flush());

        Assert.Equal(2, line.Length);
        Assert.Equal("🙂", line[0]!.Value.Text);
        Assert.Equal("x", line[1]!.Value.Text);
    }

    // ---------- \r 覆写 ----------

    [Fact]
    public void CarriageReturn_ThenShorterText_Overwrites()
    {
        var doc = new TerminalDocument();
        doc.Put("100%");
        doc.CarriageReturn();
        doc.Put("done");

        Assert.Equal("done", LineText(Assert.Single(doc.Flush())));
    }

    [Fact]
    public void CarriageReturn_ThenLongerText_Extends()
    {
        var doc = new TerminalDocument();
        doc.Put("ab");
        doc.CarriageReturn();
        doc.Put("abcdef");

        Assert.Equal("abcdef", LineText(Assert.Single(doc.Flush())));
    }

    /// <summary>进度条经典写法：<c>\r + 新文本 + EL0</c>。不擦除就会留下上一版的尾巴。</summary>
    [Fact]
    public void CrPlusEraseInLineZero_RemovesStaleTail()
    {
        var doc = new TerminalDocument();
        doc.Put("100%");
        doc.CarriageReturn();
        doc.Put("9");
        doc.EraseInLine(0);

        Assert.Equal("9", LineText(Assert.Single(doc.Flush())));
    }

    [Fact]
    public void ProgressBarSequence_EndsUpClean()
    {
        var doc = new TerminalDocument();
        foreach (var step in new[] { "10%", "50%", "100% done" })
        {
            doc.CarriageReturn();
            doc.Put(step);
            doc.EraseInLine(0);
        }

        Assert.Equal("100% done", LineText(Assert.Single(doc.Flush())));
    }

    // ---------- 光标 ----------

    [Fact]
    public void Backspace_MovesColumnLeft()
    {
        var doc = new TerminalDocument();
        doc.Put("abc");
        doc.Backspace();
        doc.Put("X");

        Assert.Equal("abX", LineText(Assert.Single(doc.Flush())));
    }

    [Fact]
    public void Backspace_AtColumnZero_DoesNotGoNegative()
    {
        var doc = new TerminalDocument();
        doc.Backspace();
        doc.Put("a");

        Assert.Equal(1, doc.CurrentColumn);
        Assert.Equal("a", LineText(Assert.Single(doc.Flush())));
    }

    [Fact]
    public void Tab_AdvancesToNextMultipleOfEight()
    {
        var doc = new TerminalDocument();
        doc.Put("a");
        doc.Tab();
        doc.Put("b");

        var line = Assert.Single(doc.Flush());
        Assert.Equal(9, line.Length);
        Assert.Equal("b", line[8]!.Value.Text);
    }

    [Fact]
    public void SetColumnForward_PadsWithSpaces()
    {
        var doc = new TerminalDocument();
        doc.Put("a");
        doc.SetColumn(3);
        doc.Put("b");

        Assert.Equal("a  b", LineText(Assert.Single(doc.Flush())));
    }

    [Fact]
    public void SetColumnBackwards_ClampsToZero()
    {
        var doc = new TerminalDocument();
        doc.Put("abc");
        doc.SetColumn(-5);
        Assert.Equal(0, doc.CurrentColumn);
    }

    [Fact]
    public void MoveColumn_Negative_ClampsAtZero()
    {
        var doc = new TerminalDocument();
        doc.Put("ab");
        doc.MoveColumn(-10);
        Assert.Equal(0, doc.CurrentColumn);
    }

    // ---------- 擦除 ----------

    [Fact]
    public void EraseInLineOne_BlanksThroughCursor()
    {
        var doc = new TerminalDocument();
        doc.Put("abcdef");
        doc.SetColumn(3);
        doc.EraseInLine(1);

        // EL 模式 1 清 0..光标（含光标），因此前 4 个字符变成空格
        Assert.Equal(new string(' ', 4) + "ef", LineText(Assert.Single(doc.Flush())));
    }

    [Fact]
    public void EraseInLineTwo_ClearsTheLine()
    {
        var doc = new TerminalDocument();
        doc.Put("abcdef");
        doc.EraseInLine(2);

        Assert.Empty(doc.Flush());
    }

    [Fact]
    public void EraseInDisplayTwo_WipesCommittedLines()
    {
        var doc = new TerminalDocument();
        doc.Put("before");
        doc.LineFeed();
        doc.Put("after");
        doc.EraseInDisplay(2);

        Assert.Empty(doc.Flush());
    }

    [Fact]
    public void EraseInDisplayZero_OnlyAffectsCurrentLine()
    {
        var doc = new TerminalDocument();
        doc.Put("committed");
        doc.LineFeed();
        doc.Put("abcdef");
        doc.EraseInDisplay(0);

        var lines = doc.Flush();
        Assert.Equal("committed", LineText(lines[0]));
        Assert.Equal("abcdef", LineText(lines[1]));
    }

    // ---------- 样式 ----------

    [Fact]
    public void Put_RecordsCurrentStyle()
    {
        var doc = new TerminalDocument();
        doc.CurrentStyle = Style.Default.WithFg(1).WithFlag(StyleFlags.Bold, true);
        doc.Put("ab");

        var line = Assert.Single(doc.Flush());
        Assert.Equal(1, line[0]!.Value.Style.Fg);
        Assert.True(line[0]!.Value.Style.Has(StyleFlags.Bold));
    }

    [Fact]
    public void StyledLine_IsUniform_WhenSingleStyle()
    {
        var doc = new TerminalDocument();
        doc.CurrentStyle = Style.Default.WithFg(2);
        doc.Put("abc");

        var line = Assert.Single(doc.Flush());
        Assert.True(line.IsUniform(out var style));
        Assert.Equal(2, style.Fg);
    }

    [Fact]
    public void StyledLine_IsNotUniform_WhenMixed()
    {
        var doc = new TerminalDocument();
        doc.Put("ab");
        doc.CurrentStyle = Style.Default.WithFg(2);
        doc.Put("cd");

        Assert.False(Assert.Single(doc.Flush()).IsUniform(out _));
    }

    // ---------- 边界 ----------

    [Fact]
    public void EmptyInput_ProducesNoLines()
    {
        var doc = new TerminalDocument();
        Assert.Empty(doc.Flush());
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var doc = new TerminalDocument();
        doc.Put("a");
        doc.LineFeed();
        doc.CurrentStyle = Style.Default.WithFg(5);
        doc.Reset();

        Assert.Empty(doc.Flush());
        Assert.Equal(Style.Default, doc.CurrentStyle);
        Assert.Equal(0, doc.CurrentColumn);
    }

    [Fact]
    public void Buffer_IsTrimmedToMaxLines()
    {
        var doc = new TerminalDocument(100);
        for (int i = 0; i < 150; i++)
        {
            doc.Put($"line-{i}");
            doc.LineFeed();
        }

        Assert.Equal(100, doc.Lines.Count);
        Assert.Equal("line-50", LineText(doc.Lines[0]));
    }
}
