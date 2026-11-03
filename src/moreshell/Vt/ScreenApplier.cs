namespace moreshell.Vt;

/// <summary>
/// L3 动作应用器：把 <see cref="VtAction"/> 翻译成 <see cref="ScreenBuffer"/> 的网格操作。
///
/// 与 L2 的 <see cref="VtApplier"/> 并存而不是替换：
/// 一次性执行仍走行式文档，只有常驻 PTY 会话使用全屏网格。
/// </summary>
public sealed class ScreenApplier
{
    private readonly ScreenBuffer _screen;

    public ScreenApplier(ScreenBuffer screen)
        => _screen = screen ?? throw new ArgumentNullException(nameof(screen));

    public ScreenBuffer Screen => _screen;

    /// <summary>需要回写给子进程的应答（DSR 光标位置、DA 设备属性等）。</summary>
    public Action<string>? Respond { get; set; }

    public void Apply(VtAction action)
    {
        switch (action.Kind)
        {
            case VtActionKind.Print:
                _screen.Put(action.Text ?? string.Empty);
                break;

            case VtActionKind.Execute:
                Execute(action.Code);
                break;

            case VtActionKind.CsiDispatch:
                Csi(action);
                break;

            case VtActionKind.EscDispatch:
                Esc(action.Code);
                break;

            case VtActionKind.OscDispatch:
                Osc(action);
                break;
        }
    }

    private void Execute(int code)
    {
        switch (code)
        {
            case '\n': case '\v': case '\f': _screen.LineFeed(); break;
            case '\r': _screen.CarriageReturn(); break;
            case '\b': _screen.Backspace(); break;
            case '\t': _screen.Tab(); break;
            default: break;   // BEL / NUL / SO / SI 等忽略
        }
    }

    private void Esc(int code)
    {
        switch (code)
        {
            case 'c': _screen.Reset(); break;          // RIS
            case '7': _screen.SaveCursor(); break;     // DECSC
            case '8': _screen.RestoreCursor(); break;  // DECRC
            case 'D': _screen.LineFeed(); break;       // IND
            case 'E': _screen.CarriageReturn(); _screen.LineFeed(); break;   // NEL
            case 'M': _screen.ReverseIndex(); break;   // RI
            default: break;                            // 键盘模式 / 字符集等忽略
        }
    }

    private void Osc(VtAction action)
    {
        if (action.Code is 0 or 2)
            _screen.SetTitle(action.Text);
    }

    private void Csi(VtAction a)
    {
        var p = a.Params ?? Array.Empty<int>();

        if (a.Private == '?')
        {
            if (a.Final is 'h' or 'l') PrivateMode(p, enabled: a.Final == 'h');
            return;
        }

        if (a.Private != '\0')
        {
            if (a.Private == '>' && a.Final == 'c') DeviceAttributes(secondary: true);   // DA2
            return;
        }

        switch (a.Final)
        {
            case 'm': _screen.CurrentStyle = Sgr.Apply(_screen.CurrentStyle, p); break;

            case 'A': _screen.MoveCursor(-Arg(p, 0, 1), 0); break;     // CUU
            case 'B': _screen.MoveCursor(Arg(p, 0, 1), 0); break;      // CUD
            case 'C': _screen.MoveCursor(0, Arg(p, 0, 1)); break;      // CUF
            case 'D': _screen.MoveCursor(0, -Arg(p, 0, 1)); break;     // CUB
            case 'E': _screen.MoveCursor(Arg(p, 0, 1), 0); _screen.SetColumn(0); break;   // CNL
            case 'F': _screen.MoveCursor(-Arg(p, 0, 1), 0); _screen.SetColumn(0); break;  // CPL
            case 'G': case '`': _screen.SetColumn(Arg(p, 0, 1) - 1); break;               // CHA / HPA
            case 'H': case 'f': _screen.SetCursor(Arg(p, 0, 1) - 1, Arg(p, 1, 1) - 1); break; // CUP
            case 'd': _screen.SetRow(Arg(p, 0, 1) - 1); break;          // VPA
            case 'e': _screen.MoveCursor(Arg(p, 0, 1), 0); break;      // VPR
            case 'a': _screen.MoveCursor(0, Arg(p, 0, 1)); break;      // HPR
            case 'Z': BackTab(Arg(p, 0, 1)); break;                    // CBT

            case 'J': _screen.EraseInDisplay(Arg(p, 0, 0)); break;     // ED
            case 'K': _screen.EraseInLine(Arg(p, 0, 0)); break;        // EL
            case 'L': _screen.InsertLines(Arg(p, 0, 1)); break;        // IL
            case 'M': _screen.DeleteLines(Arg(p, 0, 1)); break;        // DL
            case '@': _screen.InsertChars(Arg(p, 0, 1)); break;        // ICH
            case 'P': _screen.DeleteChars(Arg(p, 0, 1)); break;        // DCH
            case 'X': _screen.EraseChars(Arg(p, 0, 1)); break;         // ECH
            case 'S': _screen.ScrollUp(Arg(p, 0, 1)); break;           // SU
            case 'T': _screen.ScrollDown(Arg(p, 0, 1)); break;         // SD

            case 'r': SetScrollRegion(p); break;                       // DECSTBM
            case 's': _screen.SaveCursor(); break;
            case 'u': _screen.RestoreCursor(); break;

            case 'n': DeviceStatusReport(p); break;
            case 'c': DeviceAttributes(secondary: false); break;
            case 't': WindowReport(p); break;
            default: break;
        }
    }

    private void PrivateMode(int[] p, bool enabled)
    {
        foreach (var raw in p)
        {
            switch (VtAction.Value(raw))
            {
                case 4: _screen.InsertMode = enabled; break;
                case 7: _screen.AutoWrap = enabled; break;
                case 25: _screen.CursorVisible = enabled; break;
                case 1048:
                    if (enabled) _screen.SaveCursor();
                    else _screen.RestoreCursor();
                    break;
                case 1049:
                    if (enabled) { _screen.SaveCursor(); _screen.EnterAlternateScreen(clear: true); }
                    else { _screen.ExitAlternateScreen(clear: true); _screen.RestoreCursor(); }
                    break;
                case 1047:
                    if (enabled) _screen.EnterAlternateScreen(clear: false);
                    else _screen.ExitAlternateScreen(clear: true);
                    break;
                case 47:
                    if (enabled) _screen.EnterAlternateScreen(clear: false);
                    else _screen.ExitAlternateScreen(clear: false);
                    break;
                case 2004: _screen.BracketedPaste = enabled; break;
                default: break;   // 鼠标/光标键模式/同步输出等：本实现无需响应
            }
        }
    }

    private void SetScrollRegion(int[] p)
    {
        int top = Arg(p, 0, 1) - 1;
        int bottom = Arg(p, 1, _screen.Rows) - 1;

        if (top <= 0 && bottom >= _screen.Rows - 1)
        {
            _screen.ResetScrollRegion();
            _screen.SetCursor(0, 0);
            return;
        }

        _screen.SetScrollRegion(top, bottom);
    }

    private void BackTab(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int col = _screen.CursorColumn;
            _screen.SetColumn(col == 0 ? 0 : (col - 1) / 8 * 8);
        }
    }

    private void DeviceStatusReport(int[] p)
    {
        int mode = Arg(p, 0, 0);
        if (mode == 5) Respond?.Invoke("\x1b[0n");
        else if (mode == 6) Respond?.Invoke($"\x1b[{_screen.CursorRow + 1};{_screen.CursorColumn + 1}R");
    }

    private void DeviceAttributes(bool secondary)
    {
        // primary DA：声称是带高级视频选项的 VT100；secondary DA：xterm 风格
        Respond?.Invoke(secondary ? "\x1b[>0;10;1c" : "\x1b[?1;2c");
    }

    private void WindowReport(int[] p)
    {
        if (Arg(p, 0, 0) == 18)
            Respond?.Invoke($"\x1b[8;{_screen.Rows};{_screen.Columns}t");
    }

    private static int Arg(int[] p, int i, int def)
        => i < p.Length && VtAction.Value(p[i]) > 0 ? VtAction.Value(p[i]) : def;
}
