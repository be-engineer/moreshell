using moreshell.Vt;

namespace moreshell.Render;

/// <summary>
/// 交互模式的实时写入器。
///
/// 与行式缓冲的区别：这里<b>边到边写</b>，光标操作直接落到控制台，
/// 所以进度条（<c>\r</c> + 重绘）和流式长输出的观感与真终端一致。
///
/// 线程安全：stdout / stderr 两个后台线程都会调进来，共用同一把锁串行化，
/// 否则交错的字符会让整屏花掉。
/// </summary>
public sealed class LiveWriter
{
    private readonly object _consoleLock;
    private Style _style = Style.Default;
    private ConsoleColor? _appliedFg;
    private ConsoleColor? _appliedBg;

    public LiveWriter(object consoleLock)
        => _consoleLock = consoleLock ?? throw new ArgumentNullException(nameof(consoleLock));

    /// <summary>重定向到管道时无法移动光标，这类操作会整体跳过。</summary>
    private static bool CanMoveCursor => !Console.IsOutputRedirected;

    public void Write(VtAction action)
    {
        lock (_consoleLock)
        {
            switch (action.Kind)
            {
                case VtActionKind.Print:
                    WriteText(action.Text ?? string.Empty);
                    break;

                case VtActionKind.Execute:
                    Execute(action.Code);
                    break;

                case VtActionKind.CsiDispatch:
                    Csi(action);
                    break;

                case VtActionKind.EscDispatch:
                    if (action.Code == 'c') { _style = Style.Default; SafeClear(); }   // RIS
                    break;

                case VtActionKind.OscDispatch:
                    break;   // 标题/超链接：忽略
            }
        }
    }

    /// <summary>直接写一行提示信息（提示符、错误说明等）。</summary>
    public void WriteMessage(string text)
    {
        lock (_consoleLock) Console.WriteLine(text);
    }

    public void ResetStyle()
    {
        lock (_consoleLock) _style = Style.Default;
    }

    // ------------------------------------------------------------------

    private void WriteText(string text)
    {
        if (text.Length == 0) return;

        var (fg, bg) = ConsoleRenderer.ResolveColors(_style);
        try
        {
            // 缓存当前颜色：conhost 的 ForegroundColor/BackgroundColor 是很贵的方法调用，
            // 大量流式输出时每个块都无条件设置会明显拖慢（只在 SGR 变化时才设置）
            if (_appliedFg != fg) { Console.ForegroundColor = fg; _appliedFg = fg; }
            if (_appliedBg != bg) { Console.BackgroundColor = bg; _appliedBg = bg; }
            Console.Write(text);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
        }
    }

    private void Execute(int code)
    {
        switch (code)
        {
            case '\n': case '\v': case '\f': Console.WriteLine(); break;
            case '\r': Console.Write('\r'); break;
            case '\b': Console.Write('\b'); break;
            case '\t': Console.Write('\t'); break;
            default: break;   // BEL 等：不发声，静默忽略
        }
    }

    private void Csi(VtAction a)
    {
        if (a.Private != '\0') return;   // DECSET/DECRST：忽略

        var p = a.Params ?? Array.Empty<int>();

        switch (a.Final)
        {
            case 'm':
                _style = Sgr.Apply(_style, p);
                break;

            case 'K':   // EL
                EraseInLine(Arg(p, 0, 0));
                break;

            case 'J':   // ED
                if (Arg(p, 0, 0) >= 2) SafeClear();
                else EraseInLine(Arg(p, 0, 0));
                break;

            case 'G':   // CHA：列绝对
                SetColumn(Arg(p, 0, 1) - 1);
                break;

            case 'C':   // CUF
                SetColumn(SafeLeft() + Arg(p, 0, 1));
                break;

            case 'D':   // CUB
                SetColumn(Math.Max(0, SafeLeft() - Arg(p, 0, 1)));
                break;

            default:    // CUP 的行寻址等：L2 不支持，忽略
                break;
        }
    }

    private static int Arg(int[] p, int i, int def)
        => i < p.Length && VtAction.Value(p[i]) > 0 ? VtAction.Value(p[i]) : def;

    // ------------------------------------------------------------------
    // 光标 / 擦除：输出被重定向时全部安全降级为 no-op
    // ------------------------------------------------------------------

    private static int SafeLeft()
    {
        try { return CanMoveCursor ? Console.CursorLeft : 0; }
        catch (IOException) { return 0; }
    }

    private static void SetColumn(int col)
    {
        if (!CanMoveCursor) return;
        try
        {
            col = Math.Clamp(col, 0, Math.Max(0, Console.WindowWidth - 1));
            Console.SetCursorPosition(col, Console.CursorTop);
        }
        catch (Exception) { /* 窗口尺寸变化或已重定向 */ }
    }

    private static void SafeClear()
    {
        try { Console.Clear(); }
        catch (IOException) { }
    }

    private static void EraseInLine(int mode)
    {
        if (!CanMoveCursor) return;

        try
        {
            int width = Console.WindowWidth;
            int left = Console.CursorLeft;
            int top = Console.CursorTop;

            switch (mode)
            {
                case 0:
                    Console.Write(new string(' ', Math.Max(0, width - left - 1)));
                    Console.SetCursorPosition(left, top);
                    break;

                case 1:
                    Console.SetCursorPosition(0, top);
                    Console.Write(new string(' ', left + 1));
                    Console.SetCursorPosition(left, top);
                    break;

                default:
                    Console.SetCursorPosition(0, top);
                    Console.Write(new string(' ', width));
                    Console.SetCursorPosition(0, top);
                    break;
            }
        }
        catch (Exception) { /* 忽略：擦除只是视觉优化 */ }
    }
}
