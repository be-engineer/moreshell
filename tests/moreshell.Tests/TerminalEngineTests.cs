using System.Text;
using moreshell.Vt;

namespace moreshell.Tests;

/// <summary>
/// 字节流 → 屏幕：重点是“跨读块”的两处状态（UTF-8 多字节字符、转义序列）。
/// 这两类拆分是管道/PTY 读块边界上必然出现的。
/// </summary>
public class TerminalEngineTests
{
    [Fact]
    public void Utf8Character_SplitAcrossFeeds()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        var engine = new TerminalEngine(screen);
        var bytes = Encoding.UTF8.GetBytes("中文");

        engine.Feed(bytes.AsSpan(0, 4));   // “中”3 字节 + “文”的第 1 字节
        engine.Feed(bytes.AsSpan(4));

        Assert.Equal("中", screen.GetCell(0, 0).Text);
        Assert.Equal("文", screen.GetCell(0, 2).Text);
    }

    [Fact]
    public void EscapeSequence_SplitAcrossFeeds()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        var engine = new TerminalEngine(screen);

        engine.Feed("\x1b[3");
        engine.Feed("1mX");

        Assert.Equal("X", screen.GetCell(0, 0).Text);
        Assert.Equal(1, screen.GetCell(0, 0).Style.Fg);
    }

    [Fact]
    public void CsiCursorMove_SplitAcrossFeeds()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 5);
        var engine = new TerminalEngine(screen);

        engine.Feed("a\x1b[3");
        engine.Feed(";4Hb");

        Assert.Equal("b", screen.GetCell(2, 3).Text);
    }

    [Fact]
    public void InvalidUtf8_BecomesReplacementCharacter()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        var engine = new TerminalEngine(screen);

        engine.Feed(new byte[] { 0xFF });
        engine.Feed("x");

        Assert.Equal("\uFFFD", screen.GetCell(0, 0).Text);
        Assert.Equal("x", screen.GetCell(0, 1).Text);
    }

    [Fact]
    public void OscTitle_IsCaptured()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        var engine = new TerminalEngine(screen);

        engine.Feed("\x1b]0;hello title\x07");

        Assert.Equal("hello title", screen.Title);
    }

    [Fact]
    public void Respond_RoutesThroughCallback()
    {
        var screen = new ScreenBuffer(columns: 10, rows: 2);
        var responses = new List<string>();
        var engine = new TerminalEngine(screen, responses.Add);

        engine.Feed("\x1b[6n");

        Assert.Equal("\x1b[1;1R", Assert.Single(responses));
    }
}
