using System.Text;
using moreshell.Vt;

namespace moreshell.Tests;

/// <summary>L3 网格模型的核心行为：写入、换行、光标、擦除、滚动、备用屏。</summary>
public class ScreenBufferTests
{
    private static string RowText(ScreenBuffer screen, int row)
    {
        var sb = new StringBuilder(screen.Columns);
        for (int c = 0; c < screen.Columns; c++)
            sb.Append(screen.GetCell(row, c).Text);
        return sb.ToString();
    }

    [Fact]
    public void Put_AdvancesCursor()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 3);
        screen.Put("abc");

        Assert.Equal(0, screen.CursorRow);
        Assert.Equal(3, screen.CursorColumn);
        Assert.Equal("abc", RowText(screen, 0)[..3]);
    }

    [Fact]
    public void Put_WrapsAtRightEdge()
    {
        var screen = new ScreenBuffer(columns: 5, rows: 2);
        screen.Put("abcde");

        Assert.Equal(4, screen.CursorColumn);
        Assert.True(screen.IsDirty);

        screen.Put("f");

        Assert.Equal(1, screen.CursorRow);
        Assert.Equal(1, screen.CursorColumn);
        Assert.Equal("abcde", RowText(screen, 0));
        Assert.StartsWith("f", RowText(screen, 1));
    }

    [Fact]
    public void WideChar_OccupiesTwoCells()
    {
        var screen = new ScreenBuffer(columns: 6, rows: 2);
        screen.Put("中a");

        Assert.Equal("中", screen.GetCell(0, 0).Text);
        Assert.Equal(string.Empty, screen.GetCell(0, 1).Text);   // 续列
        Assert.Equal("a", screen.GetCell(0, 2).Text);
        Assert.Equal(3, screen.CursorColumn);
    }

    [Fact]
    public void WideChar_BreaksLineWhenOnlyOneColumnLeft()
    {
        var screen = new ScreenBuffer(columns: 4, rows: 2);
        screen.Put("abc中");

        Assert.Equal("abc", RowText(screen, 0)[..3]);
        Assert.Equal("中", screen.GetCell(1, 0).Text);
    }

    [Fact]
    public void LineFeed_AtBottom_ScrollsIntoScrollback()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 3);

        screen.SetCursor(0, 0); screen.Put("one");
        screen.SetCursor(1, 0); screen.Put("two");
        screen.SetCursor(2, 0); screen.Put("three");

        screen.CarriageReturn();
        screen.LineFeed();

        Assert.Single(screen.Scrollback);
        Assert.Equal("one", string.Concat(screen.Scrollback[0].View.Select(c => c.Text)));
        Assert.Equal(2, screen.CursorRow);
        Assert.StartsWith("two", RowText(screen, 0));
    }

    [Fact]
    public void SetCursor_ClampsToScreen()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 4);
        screen.SetCursor(99, 99);

        Assert.Equal(3, screen.CursorRow);
        Assert.Equal(9, screen.CursorColumn);
    }

    [Fact]
    public void EraseInLine_Modes()
    {
        var screen = new ScreenBuffer(columns: 6, rows: 2);

        screen.Put("abcdef");
        screen.SetColumn(2);
        screen.EraseInLine(0);
        Assert.Equal("ab    ", RowText(screen, 0));

        screen.Clear();
        screen.Put("abcdef");
        screen.SetColumn(2);
        screen.EraseInLine(1);          // 含光标本身
        Assert.Equal("   def", RowText(screen, 0));

        screen.Clear();
        screen.Put("abcdef");
        screen.EraseInLine(2);
        Assert.Equal("      ", RowText(screen, 0));
    }

    [Fact]
    public void InsertAndDeleteLines_RespectScrollRegion()
    {
        var screen = new ScreenBuffer(columns: 6, rows: 5);
        for (int r = 0; r < 5; r++)
        {
            screen.SetCursor(r, 0);
            screen.Put("r" + r);
        }

        screen.SetScrollRegion(1, 3);   // 遮蔽上下各一行
        screen.SetCursor(1, 0);
        screen.InsertLines(1);

        Assert.StartsWith("r0", RowText(screen, 0));   // 区域外不受影响
        Assert.Equal("    ", RowText(screen, 1)[..4]);
        Assert.StartsWith("r1", RowText(screen, 2));
        Assert.StartsWith("r2", RowText(screen, 3));
        Assert.StartsWith("r4", RowText(screen, 4));
    }

    [Fact]
    public void AlternateScreen_PreservesPrimary()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 3);
        screen.Put("main");

        screen.EnterAlternateScreen(clear: true);
        Assert.True(screen.AlternateScreenActive);
        Assert.Equal("    ", RowText(screen, 0)[..4]);

        screen.Put("alt");
        screen.ExitAlternateScreen(clear: true);

        Assert.False(screen.AlternateScreenActive);
        Assert.Equal("main", RowText(screen, 0)[..4]);
    }

    [Fact]
    public void Resize_KeepsTopLeftContent()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 3);
        screen.Put("hello");

        screen.Resize(5, 2);

        Assert.Equal(2, screen.Rows);
        Assert.Equal(5, screen.Columns);
        Assert.Equal("hello", RowText(screen, 0));
    }

    [Fact]
    public void Reset_RestoresDefaults()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 3);
        screen.Put("x");
        screen.CursorVisible = false;
        screen.AutoWrap = false;

        screen.Reset();

        Assert.Equal(new string(' ', 10), RowText(screen, 0));
        Assert.True(screen.CursorVisible);
        Assert.True(screen.AutoWrap);
        Assert.False(screen.AlternateScreenActive);
    }
}
