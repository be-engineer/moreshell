using moreshell.Render;
using moreshell.Vt;

namespace moreshell.Tests;

/// <summary>整帧渲染：宽字符续列、残留空单元、样式与光标定位。</summary>
public class AnsiScreenRendererTests
{
    [Fact]
    public void Render_ContainsAllRowsAndCursorPosition()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 3);
        screen.Put("hi");
        screen.SetCursor(2, 3);

        var frame = AnsiScreenRenderer.Render(screen);

        Assert.Contains("\x1b[1;1H", frame);
        Assert.Contains("\x1b[3;1H", frame);
        Assert.Contains("hi", frame);
        Assert.EndsWith("\x1b[3;4H\x1b[?25h", frame);
    }

    [Fact]
    public void Render_HidesCursorWhenInvisible()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        screen.CursorVisible = false;

        Assert.EndsWith("\x1b[?25l", AnsiScreenRenderer.Render(screen));
    }

    [Fact]
    public void Render_WideCharContinuationIsSkipped()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        screen.Put("中a");

        var frame = AnsiScreenRenderer.Render(screen);

        Assert.Contains("中a", frame);
        Assert.DoesNotContain("中 a", frame);
    }

    [Fact]
    public void Render_OrphanedEmptyCellBecomesSpace()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        screen.Put("中a");
        screen.SetColumn(0);
        screen.Put("x");            // 覆盖宽字符左半，右半留下空文本单元

        var frame = AnsiScreenRenderer.Render(screen);

        Assert.Contains("x a", frame);
    }

    [Fact]
    public void Render_EmitsSgrForColors()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        screen.CurrentStyle = new Style(StyleFlags.Bold, Fg: 9, Bg: 4);
        screen.Put("R");

        var frame = AnsiScreenRenderer.Render(screen);

        Assert.Contains("\x1b[0;1;91;44mR", frame);
    }

    [Fact]
    public void Render_EmitsTrueColor()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        screen.CurrentStyle = new Style(StyleFlags.None, Colors.Rgb(12, 34, 56), Colors.Default);
        screen.Put("T");

        var frame = AnsiScreenRenderer.Render(screen);

        Assert.Contains("\x1b[0;38;2;12;34;56mT", frame);
    }
}
