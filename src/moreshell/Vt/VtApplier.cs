namespace moreshell.Vt;

/// <summary>
/// 把 <see cref="VtAction"/> 流应用到 <see cref="TerminalDocument"/>。
///
/// 这一层集中了「转义序列 → 屏幕语义」的翻译。凡是 L2 不支持的能力（全屏行寻址、
/// 备用屏、私有 DECSET 等）都显式忽略而非报错，保证解析器喂进来的任何合法序列
/// 都不会让程序崩溃或失去同步。
/// </summary>
public sealed class VtApplier
{
    private readonly TerminalDocument _doc;

    public VtApplier(TerminalDocument doc)
        => _doc = doc ?? throw new ArgumentNullException(nameof(doc));

    public TerminalDocument Document => _doc;

    public void Apply(VtAction action)
    {
        switch (action.Kind)
        {
            case VtActionKind.Print:
                _doc.Put(action.Text ?? string.Empty);
                break;

            case VtActionKind.Execute:
                Execute(action.Code);
                break;

            case VtActionKind.CsiDispatch:
                Csi(action);
                break;

            case VtActionKind.EscDispatch:
                // RIS（ESC c）= 完全重置；其余（保存/恢复光标、键盘模式等）L2 忽略
                if (action.Code == 'c') _doc.Reset();
                break;

            case VtActionKind.OscDispatch:
                // 标题(OSC 0/2)、超链接(OSC 8)、剪贴板(OSC 52) 等：L2 忽略
                break;
        }
    }

    private void Execute(int code)
    {
        switch (code)
        {
            case '\n': case '\v': case '\f': _doc.LineFeed(); break;
            case '\r': _doc.CarriageReturn(); break;
            case '\b': _doc.Backspace(); break;
            case '\t': _doc.Tab(); break;
            default: break;   // BEL / NUL / SO / SI 等忽略
        }
    }

    private void Csi(VtAction a)
    {
        // DECSET/DECRST（?25h 光标显隐、?1049h 备用屏、?2004h 括号粘贴 …）：L2 一律忽略
        if (a.Private != '\0') return;

        var p = a.Params ?? Array.Empty<int>();

        switch (a.Final)
        {
            case 'm': _doc.CurrentStyle = Sgr.Apply(_doc.CurrentStyle, p); break;   // SGR
            case 'K': _doc.EraseInLine(Arg(p, 0, 0)); break;                         // EL
            case 'J': _doc.EraseInDisplay(Arg(p, 0, 0)); break;                      // ED
            case 'G': _doc.SetColumn(Arg(p, 0, 1) - 1); break;                       // CHA 列绝对
            case 'C': _doc.MoveColumn(Arg(p, 0, 1)); break;                          // CUF 列+
            case 'D': _doc.MoveColumn(-Arg(p, 0, 1)); break;                         // CUB 列-
            case 'H': case 'f': _doc.SetColumn(Arg(p, 1, 1) - 1); break;             // CUP：只取列，行寻址 L2 不支持
            case 'A': case 'B': break;                                               // 行移动：忽略
            default: break;                                                          // 增删行/字符等：忽略
        }
    }

    /// <summary>取参数值；0 或缺省时用默认值（多数 CSI 里 0 表示「按 1 处理」）。</summary>
    private static int Arg(int[] p, int i, int def)
        => i < p.Length && VtAction.Value(p[i]) > 0 ? VtAction.Value(p[i]) : def;
}
