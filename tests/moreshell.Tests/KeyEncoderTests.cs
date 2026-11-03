using moreshell.Input;

namespace moreshell.Tests;

/// <summary>按键 → xterm 输入序列。</summary>
public class KeyEncoderTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0',
        bool shift = false, bool alt = false, bool ctrl = false)
        => new(ch, key, shift, alt, ctrl);

    [Fact]
    public void ArrowKeys_UseCsi()
    {
        Assert.Equal("\x1b[A", KeyEncoder.Encode(Key(ConsoleKey.UpArrow)));
        Assert.Equal("\x1b[B", KeyEncoder.Encode(Key(ConsoleKey.DownArrow)));
        Assert.Equal("\x1b[C", KeyEncoder.Encode(Key(ConsoleKey.RightArrow)));
        Assert.Equal("\x1b[D", KeyEncoder.Encode(Key(ConsoleKey.LeftArrow)));
    }

    [Fact]
    public void ArrowKeys_WithModifiers()
    {
        Assert.Equal("\x1b[1;2A", KeyEncoder.Encode(Key(ConsoleKey.UpArrow, shift: true)));
        Assert.Equal("\x1b[1;3B", KeyEncoder.Encode(Key(ConsoleKey.DownArrow, alt: true)));
        Assert.Equal("\x1b[1;5C", KeyEncoder.Encode(Key(ConsoleKey.RightArrow, ctrl: true)));
        Assert.Equal("\x1b[1;6D", KeyEncoder.Encode(Key(ConsoleKey.LeftArrow, shift: true, ctrl: true)));
    }

    [Fact]
    public void HomeAndEnd()
    {
        Assert.Equal("\x1b[H", KeyEncoder.Encode(Key(ConsoleKey.Home)));
        Assert.Equal("\x1b[1;2F", KeyEncoder.Encode(Key(ConsoleKey.End, shift: true)));
    }

    [Fact]
    public void TildeKeys()
    {
        Assert.Equal("\x1b[2~", KeyEncoder.Encode(Key(ConsoleKey.Insert)));
        Assert.Equal("\x1b[3~", KeyEncoder.Encode(Key(ConsoleKey.Delete)));
        Assert.Equal("\x1b[5~", KeyEncoder.Encode(Key(ConsoleKey.PageUp)));
        Assert.Equal("\x1b[6;5~", KeyEncoder.Encode(Key(ConsoleKey.PageDown, ctrl: true)));
    }

    [Fact]
    public void FunctionKeys()
    {
        Assert.Equal("\x1bOP", KeyEncoder.Encode(Key(ConsoleKey.F1)));
        Assert.Equal("\x1b[15~", KeyEncoder.Encode(Key(ConsoleKey.F5)));
        Assert.Equal("\x1b[15;5~", KeyEncoder.Encode(Key(ConsoleKey.F5, ctrl: true)));
        Assert.Equal("\x1b[24~", KeyEncoder.Encode(Key(ConsoleKey.F12)));
    }

    [Fact]
    public void TabAndEnter()
    {
        Assert.Equal("\t", KeyEncoder.Encode(Key(ConsoleKey.Tab)));
        Assert.Equal("\x1b[Z", KeyEncoder.Encode(Key(ConsoleKey.Tab, shift: true)));
        Assert.Equal("\r", KeyEncoder.Encode(Key(ConsoleKey.Enter, ch: '\r')));
        Assert.Equal("\x1b\r", KeyEncoder.Encode(Key(ConsoleKey.Enter, ch: '\r', alt: true)));
    }

    [Fact]
    public void ControlCharacters()
    {
        Assert.Equal("\x03", KeyEncoder.Encode(Key(ConsoleKey.C, ch: 'c', ctrl: true)));
        Assert.Equal("\x01", KeyEncoder.Encode(Key(ConsoleKey.A, ch: 'a', ctrl: true)));
        Assert.Equal("\0", KeyEncoder.Encode(Key(ConsoleKey.Spacebar, ch: ' ', ctrl: true)));
    }

    [Fact]
    public void AltPrefixesEscape()
    {
        Assert.Equal("\x1bx", KeyEncoder.Encode(Key(ConsoleKey.X, ch: 'x', alt: true)));
    }

    [Fact]
    public void PlainCharacters_PassThrough()
    {
        Assert.Equal("a", KeyEncoder.Encode(Key(ConsoleKey.A, ch: 'a')));
        Assert.Equal("\x1b", KeyEncoder.Encode(Key(ConsoleKey.Escape)));
        Assert.Equal("\x7f", KeyEncoder.Encode(Key(ConsoleKey.Backspace)));
    }
}
