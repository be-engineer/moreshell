namespace moreshell.Input;

/// <summary>
/// 控制台按键 → PTY 输入字节的编码器。
///
/// 常驻 shell 拿到的是“真终端”输入，所以方向键/功能键必须编码成 xterm 序列，
/// 而不是把 <see cref="ConsoleKey"/> 的名字发给它。Ctrl+A..Z 则还原成 C0 控制码。
/// </summary>
public static class KeyEncoder
{
    /// <summary>编码一个按键；返回 null 表示该键不产生输入。</summary>
    public static string? Encode(ConsoleKeyInfo key)
    {
        int mod = ModifierBits(key);
        string? sequence = EncodeSpecial(key.Key, mod);

        if (sequence is not null)
            return sequence;

        bool ctrl = (mod & (int)Modifier.Ctrl) != 0;
        bool alt = (mod & (int)Modifier.Alt) != 0;

        if (ctrl && key.Key == ConsoleKey.Spacebar)
            return alt ? "\x1b\0" : "\0";

        if (key.KeyChar == '\0')
            return null;

        if (ctrl && TryControl(key.KeyChar, out char control))
            return (alt ? "\x1b" : "") + control;

        return (alt ? "\x1b" : "") + key.KeyChar;
    }

    private static bool TryControl(char ch, out char control)
    {
        control = '\0';
        if (ch == '\0') { control = '\0'; return true; }   // Ctrl+Space
        char upper = char.ToUpperInvariant(ch);
        if (upper >= 'A' && upper <= 'Z')
        {
            control = (char)(upper - 'A' + 1);
            return true;
        }
        if (ch is '[' or '\\' or ']' or '^' or '_' or '@')
        {
            control = ch switch
            {
                '[' => '\x1b',
                '\\' => '\x1c',
                ']' => '\x1d',
                '^' => '\x1e',
                '_' => '\x1f',
                _ => '\0',
            };
            return true;
        }
        return false;
    }

    private static string? EncodeSpecial(ConsoleKey key, int mod)
    {
        string? result = key switch
        {
            ConsoleKey.UpArrow => Cursor("A", mod),
            ConsoleKey.DownArrow => Cursor("B", mod),
            ConsoleKey.RightArrow => Cursor("C", mod),
            ConsoleKey.LeftArrow => Cursor("D", mod),
            ConsoleKey.Home => Cursor("H", mod),
            ConsoleKey.End => Cursor("F", mod),
            ConsoleKey.Insert => Tilde(2, mod),
            ConsoleKey.Delete => Tilde(3, mod),
            ConsoleKey.PageUp => Tilde(5, mod),
            ConsoleKey.PageDown => Tilde(6, mod),
            ConsoleKey.F1 => Function(1, mod),
            ConsoleKey.F2 => Function(2, mod),
            ConsoleKey.F3 => Function(3, mod),
            ConsoleKey.F4 => Function(4, mod),
            ConsoleKey.F5 => Function(5, mod),
            ConsoleKey.F6 => Function(6, mod),
            ConsoleKey.F7 => Function(7, mod),
            ConsoleKey.F8 => Function(8, mod),
            ConsoleKey.F9 => Function(9, mod),
            ConsoleKey.F10 => Function(10, mod),
            ConsoleKey.F11 => Function(11, mod),
            ConsoleKey.F12 => Function(12, mod),
            ConsoleKey.Tab => (mod & (int)Modifier.Shift) != 0 ? "\x1b[Z" : "\t",
            ConsoleKey.Enter => (mod & (int)Modifier.Alt) != 0 ? "\x1b\r" : "\r",
            ConsoleKey.Backspace => "\x7f",
            ConsoleKey.Escape => "\x1b",
            _ => null,
        };

        // 普通字符类按键交给 KeyChar 路径，避免把可打印字符重复编码
        if (result is null) return null;

        return result;
    }

    private static string Cursor(string final, int mod)
        => mod == 0 ? "\x1b[" + final : "\x1b[1;" + (mod + 1) + final;

    private static string Tilde(int code, int mod)
        => mod == 0 ? "\x1b[" + code + "~" : "\x1b[" + code + ";" + (mod + 1) + "~";

    private static string Function(int n, int mod)
    {
        // F1-F4 是 SS3（无修饰）或 CSI 1;mP；F5 起是 CSI n~ 形式
        if (n <= 4)
        {
            char final = (char)('P' + n - 1);
            return mod == 0 ? "\x1bO" + final : "\x1b[1;" + (mod + 1) + final;
        }

        int code = n switch
        {
            5 => 15,
            6 => 17,
            7 => 18,
            8 => 19,
            9 => 20,
            10 => 21,
            11 => 23,
            _ => 24,
        };
        return Tilde(code, mod);
    }

    private static int ModifierBits(ConsoleKeyInfo key)
    {
        int bits = 0;
        if (key.Modifiers.HasFlag(ConsoleModifiers.Shift)) bits |= (int)Modifier.Shift;
        if (key.Modifiers.HasFlag(ConsoleModifiers.Alt)) bits |= (int)Modifier.Alt;
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control)) bits |= (int)Modifier.Ctrl;
        return bits;
    }

    [Flags]
    private enum Modifier
    {
        None = 0,
        Shift = 1,
        Alt = 2,
        Ctrl = 4,
    }
}
