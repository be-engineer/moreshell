using moreshell.Vt;

namespace moreshell.Tests;

/// <summary>动作 → 网格：CSI 光标、擦除、模式切换与终端应答。</summary>
public class ScreenApplierTests
{
    private static (ScreenBuffer Screen, ScreenApplier Applier, List<string> Responses) Create(int columns = 20, int rows = 6)
    {
        var screen = new ScreenBuffer(columns, rows);
        var applier = new ScreenApplier(screen);
        var responses = new List<string>();
        applier.Respond = responses.Add;
        return (screen, applier, responses);
    }

    private static void Feed(ScreenApplier applier, string text)
    {
        var decoder = new VtDecoder();
        decoder.Decode(text, applier.Apply);
    }

    [Fact]
    public void Cup_MovesCursor()
    {
        var (screen, applier, _) = Create();
        Feed(applier, "\x1b[3;5H");

        Assert.Equal(2, screen.CursorRow);
        Assert.Equal(4, screen.CursorColumn);
    }

    [Fact]
    public void RelativeMoves_AreClamped()
    {
        var (screen, applier, _) = Create();
        Feed(applier, "\x1b[2;2H\x1b[99A");
        Assert.Equal(0, screen.CursorRow);
        Assert.Equal(1, screen.CursorColumn);

        Feed(applier, "\x1b[99C");
        Assert.Equal(19, screen.CursorColumn);
    }

    [Fact]
    public void EraseInDisplay_ClearsScreen()
    {
        var (screen, applier, _) = Create(5, 3);
        Feed(applier, "abc\x1b[2J");

        Assert.Equal("     ", Text(screen, 0));
        Assert.Equal(0, screen.CursorRow);
        Assert.Equal(3, screen.CursorColumn);   // ED 不清光标
    }

    [Fact]
    public void Sgr_SetsStyleOnFollowingText()
    {
        var (screen, applier, _) = Create();
        Feed(applier, "\x1b[1;31mred");

        var cell = screen.GetCell(0, 0);
        Assert.Equal("r", cell.Text);
        Assert.Equal(1, cell.Style.Fg);
        Assert.True(cell.Style.Has(StyleFlags.Bold));
    }

    [Fact]
    public void DeviceStatusReport_RespondsWithCursorPosition()
    {
        var (_, applier, responses) = Create();
        Feed(applier, "\x1b[5;7H\x1b[6n");

        Assert.Equal("\x1b[5;7R", Assert.Single(responses));
    }

    [Fact]
    public void DeviceAttributes_RespondAsXterm()
    {
        var (_, applier, responses) = Create();
        Feed(applier, "\x1b[c\x1b[>c");

        Assert.Equal(new[] { "\x1b[?1;2c", "\x1b[>0;10;1c" }, responses);
    }

    [Fact]
    public void WindowSizeReport_ReturnsScreenDimensions()
    {
        var (_, applier, responses) = Create(columns: 132, rows: 43);
        Feed(applier, "\x1b[18t");

        Assert.Equal("\x1b[8;43;132t", Assert.Single(responses));
    }

    [Fact]
    public void AlternateScreenModes_1049_Toggle()
    {
        var (screen, applier, _) = Create();
        Feed(applier, "main\x1b[?1049h");
        Assert.True(screen.AlternateScreenActive);

        Feed(applier, "alt\x1b[?1049l");
        Assert.False(screen.AlternateScreenActive);
        Assert.Equal("m", screen.GetCell(0, 0).Text);
    }

    [Fact]
    public void CursorVisibilityMode_IsTracked()
    {
        var (screen, applier, _) = Create();
        Feed(applier, "\x1b[?25l");
        Assert.False(screen.CursorVisible);

        Feed(applier, "\x1b[?25h");
        Assert.True(screen.CursorVisible);
    }

    [Fact]
    public void ScrollRegion_ConfinesLineFeed()
    {
        var (screen, applier, _) = Create(10, 5);
        Feed(applier, "\x1b[1;1Htop");
        Feed(applier, "\x1b[2;1Hr1");
        Feed(applier, "\x1b[3;1Hr2");
        Feed(applier, "\x1b[4;1Hr3");
        Feed(applier, "\x1b[5;1Hbottom");

        // 区域 2..4；光标在第 4 行时换行只滚动区域内容
        Feed(applier, "\x1b[2;4r\x1b[4;1H\n");

        Assert.Equal("t", screen.GetCell(0, 0).Text);
        Assert.Equal("r2", Text(screen, 1)[..2]);
        Assert.Equal("r3", Text(screen, 2)[..2]);
        Assert.Equal("b", screen.GetCell(4, 0).Text);
        Assert.Empty(screen.Scrollback);   // 滚动区域不产生回滚
    }

    private static string Text(ScreenBuffer screen, int row)
    {
        var chars = new char[screen.Columns];
        for (int c = 0; c < chars.Length; c++)
            chars[c] = screen.GetCell(row, c).Text.Length == 0 ? ' ' : screen.GetCell(row, c).Text[0];
        return new string(chars);
    }
}
