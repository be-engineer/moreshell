using moreshell.Vt;

namespace moreshell.Render;

/// <summary>一行的渲染结果：一段同色文本 + 最终样式。</summary>
public readonly record struct RenderSegment(string Text, ConsoleColor Fg, ConsoleColor Bg, StyleFlags Flags);

/// <summary>
/// 把 <see cref="StyledLine"/> 画到控制台。
///
/// 关键点：颜色是**我们自己的解析器算出来的**，不是把子进程的 ANSI 原样透传。
/// 因此 256 色 / 真彩色必须先降采样到 Windows 控制台的 16 色，
/// 这一步正是"解析是否正确"的可观察证据。
///
/// <see cref="Segment"/> 是纯函数，单元测试直接断言它的结果；
/// <see cref="Write"/> 只是把它写进 Console，便于交互观察。
/// </summary>
public static class ConsoleRenderer
{
    /// <summary>
    /// ANSI 调色板索引 → <see cref="ConsoleColor"/>。
    /// 注意：两者数值**不相等**。Windows 控制台属性位是 bit0=R, bit1=G, bit2=B, bit3=强度，
    /// 而 ANSI 是 0黑 1红 2绿 3黄 4蓝 5品红 6青 7白，必须查表转换。
    /// </summary>
    private static readonly ConsoleColor[] AnsiToConsole =
    {
        ConsoleColor.Black,      // 0
        ConsoleColor.DarkRed,    // 1
        ConsoleColor.DarkGreen,  // 2
        ConsoleColor.DarkYellow, // 3
        ConsoleColor.DarkBlue,   // 4
        ConsoleColor.DarkMagenta,// 5
        ConsoleColor.DarkCyan,   // 6
        ConsoleColor.Gray,       // 7
        ConsoleColor.DarkGray,   // 8  bright black
        ConsoleColor.Red,        // 9
        ConsoleColor.Green,      // 10
        ConsoleColor.Yellow,     // 11
        ConsoleColor.Blue,       // 12
        ConsoleColor.Magenta,    // 13
        ConsoleColor.Cyan,       // 14
        ConsoleColor.White,      // 15
    };

    /// <summary>ANSI 0-15 的 RGB（xterm 基础色），用于 256 色/真彩色的最近邻降采样。</summary>
    private static readonly (int R, int G, int B)[] Base16Rgb =
    {
        (0x00, 0x00, 0x00), (0x80, 0x00, 0x00), (0x00, 0x80, 0x00), (0x80, 0x80, 0x00),
        (0x00, 0x00, 0x80), (0x80, 0x00, 0x80), (0x00, 0x80, 0x80), (0xC0, 0xC0, 0xC0),
        (0x80, 0x80, 0x80), (0xFF, 0x00, 0x00), (0x00, 0xFF, 0x00), (0xFF, 0xFF, 0x00),
        (0x00, 0x00, 0xFF), (0xFF, 0x00, 0xFF), (0x00, 0xFF, 0xFF), (0xFF, 0xFF, 0xFF),
    };

    /// <summary>把一行切成同色段落。</summary>
    public static IReadOnlyList<RenderSegment> Segment(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var cells = line.View;
        var result = new List<RenderSegment>();

        var buffer = new System.Text.StringBuilder();
        ConsoleColor curFg = default, curBg = default;
        StyleFlags curFlags = default;
        bool started = false;

        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var (fg, bg) = ResolveColors(cell.Style);

            if (!started)
            {
                curFg = fg; curBg = bg; curFlags = cell.Style.Flags;
                started = true;
            }
            else if (fg != curFg || bg != curBg || cell.Style.Flags != curFlags)
            {
                result.Add(new RenderSegment(buffer.ToString(), curFg, curBg, curFlags));
                buffer.Clear();
                curFg = fg; curBg = bg; curFlags = cell.Style.Flags;
            }

            buffer.Append(cell.Text);
        }

        if (started)
            result.Add(new RenderSegment(buffer.ToString(), curFg, curBg, curFlags));

        return result;
    }

    /// <summary>写一行到控制台（颜色来自本类的解析结果）。</summary>
    public static void Write(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var segments = Segment(line);
        if (segments.Count == 0)
        {
            Console.WriteLine();
            return;
        }

        var oldFg = Console.ForegroundColor;
        var oldBg = Console.BackgroundColor;
        try
        {
            foreach (var seg in segments)
            {
                Console.ForegroundColor = seg.Fg;
                Console.BackgroundColor = seg.Bg;
                Console.Write(seg.Text);
            }
        }
        finally
        {
            Console.ForegroundColor = oldFg;
            Console.BackgroundColor = oldBg;
        }
        Console.WriteLine();
    }

    /// <summary>
    /// 样式 → 实际控制台前景/背景色。
    /// 处理顺序有讲究：先做 INVERSE 交换，再做 BOLD 提亮，最后降采样。
    /// </summary>
    public static (ConsoleColor Fg, ConsoleColor Bg) ResolveColors(Style style)
    {
        int fg = style.Fg;
        int bg = style.Bg;

        if (style.Has(StyleFlags.Inverse))
            (fg, bg) = (bg, fg);

        if (style.Has(StyleFlags.Hidden))
            fg = bg;           // 真终端的"隐藏"是把字画成和背景同色

        // BOLD 对 0-7 号色的标准含义是"换到对应的 8-15 号亮色"
        if (style.Has(StyleFlags.Bold) && fg >= 0 && fg < 8 && !Colors.IsTrueColor(fg))
            fg += 8;

        return (ToConsole(fg, isBackground: false), ToConsole(bg, isBackground: true));
    }

    /// <summary>任意形态的颜色 → 16 色 <see cref="ConsoleColor"/>。</summary>
    public static ConsoleColor ToConsole(int color, bool isBackground)
    {
        if (Colors.IsDefault(color))
            return isBackground ? ConsoleColor.Black : ConsoleColor.Gray;   // 终端默认：黑底灰字

        if (color < 16)
            return AnsiToConsole[color];

        var (r, g, b) = GetRgb(color);
        return NearestOf16(r, g, b);
    }

    private static (int R, int G, int B) GetRgb(int color)
    {
        if (Colors.IsTrueColor(color))
        {
            int v = color - Colors.TrueColorBase;
            return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        // 256 色调色板
        int idx = Math.Clamp(color, 0, 255);
        if (idx < 16) return Base16Rgb[idx];

        if (idx < 232)                      // 6x6x6 立方
        {
            int i = idx - 16;
            static int Cube(int n) => n == 0 ? 0 : 55 + n * 40;
            return (Cube(i / 36), Cube((i / 6) % 6), Cube(i % 6));
        }

        int gray = 8 + (idx - 232) * 10;    // 灰度梯
        return (gray, gray, gray);
    }

    private static ConsoleColor NearestOf16(int r, int g, int b)
    {
        int best = 0;
        long bestDist = long.MaxValue;

        for (int i = 0; i < Base16Rgb.Length; i++)
        {
            long dr = r - Base16Rgb[i].R;
            long dg = g - Base16Rgb[i].G;
            long db = b - Base16Rgb[i].B;
            long dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist) { bestDist = dist; best = i; }
        }

        return AnsiToConsole[best];
    }
}
