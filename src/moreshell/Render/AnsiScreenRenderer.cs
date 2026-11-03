using System.Text;
using moreshell.Vt;

namespace moreshell.Render;

/// <summary>
/// L3 全屏渲染器：把 <see cref="ScreenBuffer"/> 画成一段 ANSI 帧。
///
/// 采用整屏重绘而不是逐字符增量：一屏 80×24 约几 KB，代价可以忽略，
/// 换来的是不需要维护复杂的脏区状态——全屏程序滚动/换屏时尤其省心。
/// </summary>
public static class AnsiScreenRenderer
{
    /// <summary>生成完整一帧（包含隐藏/恢复光标与最终光标定位）。</summary>
    public static string Render(ScreenBuffer screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var sb = new StringBuilder(screen.Columns * screen.Rows * 2 + 256);

        sb.Append("\x1b[?25l");                       // 绘制期间隐藏光标，避免闪烁

        Style? currentStyle = null;
        for (int row = 0; row < screen.Rows; row++)
        {
            sb.Append("\x1b[").Append(row + 1).Append(";1H");

            for (int col = 0; col < screen.Columns; col++)
            {
                var cell = screen.GetCell(row, col);

                // 空文本单元只有在紧跟双宽字符时才是“续列”，其余情况必须画空格，
                // 否则后面所有列都会左移一格，屏幕上留下上一帧的残影。
                if (cell.Text.Length == 0 && col > 0 && IsWide(screen.GetCell(row, col - 1)))
                    continue;

                if (currentStyle is null || currentStyle.Value != cell.Style)
                {
                    AppendSgr(sb, cell.Style);
                    currentStyle = cell.Style;
                }

                sb.Append(cell.Text.Length == 0 ? " " : cell.Text);
            }

            sb.Append("\x1b[0m\x1b[K");                // 清掉上一帧可能残留的尾部内容
            currentStyle = null;
        }

        sb.Append("\x1b[0m");
        sb.Append("\x1b[").Append(screen.CursorRow + 1).Append(';')
          .Append(screen.CursorColumn + 1).Append('H');
        sb.Append(screen.CursorVisible ? "\x1b[?25h" : "\x1b[?25l");

        return sb.ToString();
    }

    public static void RenderTo(TextWriter writer, ScreenBuffer screen)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Render(screen));
        writer.Flush();
    }

    /// <summary>该单元是否是双宽字符（其右侧一格为续列）。</summary>
    private static bool IsWide(Cell cell)
        => cell.Text.Length > 0
           && Rune.DecodeFromUtf16(cell.Text, out var rune, out _) == System.Buffers.OperationStatus.Done
           && ScreenBuffer.RuneWidth(rune) == 2;

    /// <summary>样式 → SGR 序列。先发 0（重置）再叠加属性，避免状态泄漏。</summary>
    public static void AppendSgr(StringBuilder sb, Style style)
    {
        sb.Append("\x1b[0");
        AppendFlags(sb, style);
        AppendColor(sb, style.Fg, background: false);
        AppendColor(sb, style.Bg, background: true);
        sb.Append('m');
    }

    private static void AppendFlags(StringBuilder sb, Style style)
    {
        if (style.Has(StyleFlags.Bold)) sb.Append(";1");
        if (style.Has(StyleFlags.Dim)) sb.Append(";2");
        if (style.Has(StyleFlags.Italic)) sb.Append(";3");
        if (style.Has(StyleFlags.Underline)) sb.Append(";4");
        if (style.Has(StyleFlags.Blink)) sb.Append(";5");
        if (style.Has(StyleFlags.Inverse)) sb.Append(";7");
        if (style.Has(StyleFlags.Hidden)) sb.Append(";8");
        if (style.Has(StyleFlags.Strike)) sb.Append(";9");
    }

    private static void AppendColor(StringBuilder sb, int color, bool background)
    {
        if (Colors.IsDefault(color)) return;

        if (!Colors.IsTrueColor(color) && color < 8)
        {
            sb.Append(';').Append((background ? 40 : 30) + color);
            return;
        }

        if (!Colors.IsTrueColor(color) && color < 16)
        {
            sb.Append(';').Append((background ? 100 : 90) + color - 8);
            return;
        }

        if (Colors.IsTrueColor(color))
        {
            int v = color - Colors.TrueColorBase;
            sb.Append(';').Append(background ? 48 : 38).Append(";2;")
              .Append((v >> 16) & 0xFF).Append(';')
              .Append((v >> 8) & 0xFF).Append(';')
              .Append(v & 0xFF);
            return;
        }

        sb.Append(';').Append(background ? 48 : 38).Append(";5;").Append(Math.Clamp(color, 0, 255));
    }
}
