using System.Text;

namespace moreshell.Vt;

/// <summary>
/// 手写的 VT/ANSI 转义序列解析器（状态机）。
///
/// 覆盖 ECMA-48 常见子集：CSI（SGR / EL / ED / 列移动）、OSC、DCS/SOS/PM/APC 字符串、
/// 以及 C0/C1 控制字符。未知序列会被安全忽略，解析器不会失去同步。
///
/// 关键设计：
/// 1. **有状态**——转义序列可能被拆分在多次 <see cref="Decode"/> 调用之间（管道分块读取时必然发生），
///    状态跨调用保留，因此不会把半截序列切成乱码。
/// 2. **产出动作而非直接改屏幕**——同一份动作流，L2 喂给行式日志，
///    升级 L3 时直接喂全屏网格，解析器零改动。
/// </summary>
public sealed class VtDecoder
{
    private enum S { Ground, Escape, Csi, Osc, Str, StrEsc }

    private const int MaxStringLength = 4096;

    private S _state = S.Ground;
    private readonly StringBuilder _print = new(256);
    private readonly StringBuilder _str = new(64);
    private readonly List<int> _params = new(8);
    private int _cur = -1;
    private bool _sawSep;
    private char _private;
    private char _intermediate;
    private bool _isOsc;

    /// <summary>当前是否正处在某个转义序列中间（用于测试断言与调试）。</summary>
    public bool InSequence => _state != S.Ground;

    /// <summary>当前状态名（调试用）。</summary>
    public string StateName => _state.ToString();

    public void Reset()
    {
        _state = S.Ground;
        _print.Clear();
        _str.Clear();
        _params.Clear();
        _cur = -1;
        _sawSep = false;
        _private = '\0';
        _intermediate = '\0';
        _isOsc = false;
    }

    /// <summary>喂入一段已解码文本。序列可跨调用拆分。</summary>
    public void Decode(string text, Action<VtAction> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        if (string.IsNullOrEmpty(text)) return;

        foreach (var ch in text)
        {
            if (_state == S.Ground && IsPrintable(ch))
            {
                _print.Append(ch);
                continue;
            }
            FlushPrint(emit);
            Step(ch, emit);
        }
        FlushPrint(emit);
    }

    private void FlushPrint(Action<VtAction> emit)
    {
        if (_print.Length == 0) return;
        emit(VtAction.Print(_print.ToString()));
        _print.Clear();
    }

    private void Step(char ch, Action<VtAction> emit)
    {
        switch (_state)
        {
            case S.Ground: Ground(ch, emit); break;
            case S.Escape: Escape(ch, emit); break;
            case S.Csi: Csi(ch, emit); break;
            case S.Osc: OscString(ch, emit); break;
            case S.Str: Str(ch, emit); break;
            case S.StrEsc: StrEsc(ch, emit); break;
        }
    }

    /// <summary>可打印字符：排除 DEL(0x7F) 与 C1 控制区(0x80-0x9F)。</summary>
    private static bool IsPrintable(char ch)
        => ch >= 0x20 && ch != 0x7F && (ch < 0x80 || ch >= 0xA0);

    // ---------------- Ground ----------------

    private void Ground(char ch, Action<VtAction> emit)
    {
        switch (ch)
        {
            case '\x1B': _state = S.Escape; return;   // ESC
            case '\x9B': BeginCsi(); return;          // 8-bit CSI
            case '\x9D': BeginOsc(); return;          // 8-bit OSC
            case '\x90':                              // DCS
            case '\x98':                              // SOS
            case '\x9E':                              // PM
            case '\x9F':                              // APC
                _state = S.Str;
                _isOsc = false;
                _str.Clear();
                return;
            case '\x9C': return;                      // 游离 ST，忽略
            default:
                emit(VtAction.Execute(ch));
                return;
        }
    }

    // ---------------- ESC ... ----------------

    private void Escape(char ch, Action<VtAction> emit)
    {
        switch (ch)
        {
            case '[': BeginCsi(); return;
            case ']': BeginOsc(); return;
            case 'P': case 'X': case '^': case '_':
                _state = S.Str;
                _isOsc = false;
                _str.Clear();
                return;
            case '\x1B': return;                      // 连续 ESC，保持在 Escape
            case '\x9C': _state = S.Ground; return;   // ST
            default:
                _state = S.Ground;
                _str.Clear();
                emit(new VtAction { Kind = VtActionKind.EscDispatch, Code = ch });
                return;
        }
    }

    // ---------------- CSI ... ----------------

    private void BeginCsi()
    {
        _state = S.Csi;
        _params.Clear();
        _cur = -1;
        _sawSep = false;
        _private = '\0';
        _intermediate = '\0';
    }

    private void Csi(char ch, Action<VtAction> emit)
    {
        if (ch >= '0' && ch <= '9')
        {
            if (_cur < 0) _cur = 0;
            _cur = _cur * 10 + (ch - '0');
            if (_cur > 65535) _cur = 65535;          // 防御：参数上限
            return;
        }

        if (ch == ';' || ch == ':')
        {
            int v = _cur >= 0 ? _cur : 0;
            // 保留分隔符种类：冒号形式可能带 colorSpaceId，见 VtAction.ColonSeparator
            if (ch == ':') v |= VtAction.ColonSeparator;
            _params.Add(v);
            if (_params.Count >= 64) _params.RemoveAt(0);  // 防御
            _cur = -1;
            _sawSep = true;
            return;
        }

        if (ch is '?' or '<' or '>' or '=' or '\'' or '"')
        {
            if (_params.Count == 0 && !_sawSep && _private == '\0') _private = ch;
            return;
        }

        if (ch >= 0x20 && ch <= 0x2F)
        {
            if (_intermediate == '\0') _intermediate = ch;
            return;
        }

        if (ch >= 0x40 && ch <= 0x7E)
        {
            PushTrailingParam();
            var action = new VtAction
            {
                Kind = VtActionKind.CsiDispatch,
                Params = _params.ToArray(),
                Final = ch,
                Private = _private,
                Intermediate = _intermediate,
            };
            _state = S.Ground;
            emit(action);
            return;
        }

        if (ch == '\x1B') { _state = S.Escape; return; }

        emit(VtAction.Execute(ch));                   // CSI 内嵌 C0 控制字符
    }

    private void PushTrailingParam()
    {
        if (_cur >= 0) _params.Add(_cur);
        else if (_sawSep) _params.Add(0);             // 形如 [1;m 的空尾参数
        _cur = -1;
    }

    // ---------------- OSC ... ----------------

    private void BeginOsc()
    {
        _state = S.Osc;
        _str.Clear();
        _isOsc = true;
    }

    private void OscString(char ch, Action<VtAction> emit)
    {
        if (ch == '\a') { EndOsc(emit); return; }     // BEL 终止
        if (ch == '\x9C') { EndOsc(emit); return; }   // 8-bit ST
        if (ch == '\x1B') { _state = S.StrEsc; return; }

        _str.Append(ch);
        if (_str.Length > MaxStringLength)
        {
            // 超长：降级为“丢弃到终止符”，避免无界内存
            _state = S.Str;
            _isOsc = false;
        }
    }

    private void EndOsc(Action<VtAction> emit)
    {
        if (_isOsc)
        {
            var s = _str.ToString();
            int semi = s.IndexOf(';');
            int code = -1;
            string text;
            if (semi >= 0)
            {
                int.TryParse(s.AsSpan(0, semi), out code);
                text = s[(semi + 1)..];
            }
            else
            {
                int.TryParse(s, out code);
                text = string.Empty;
            }

            if (code >= 0)
                emit(new VtAction { Kind = VtActionKind.OscDispatch, Code = code, Text = text });
        }
        _str.Clear();
        _isOsc = false;
        _state = S.Ground;
    }

    // ---------------- DCS / SOS / PM / APC ----------------

    private void Str(char ch, Action<VtAction> emit)
    {
        if (ch == '\x1B') { _state = S.StrEsc; return; }
        if (ch == '\x9C') { _state = S.Ground; _str.Clear(); _isOsc = false; return; }
        // 内容一律丢弃（L2 不支持 DCS 回应）
    }

    private void StrEsc(char ch, Action<VtAction> emit)
    {
        if (ch == '\\')                                // ST 终止
        {
            if (_isOsc) EndOsc(emit);
            else { _state = S.Ground; _str.Clear(); _isOsc = false; }
            return;
        }
        _state = S.Escape;
        Escape(ch, emit);                              // 不是 ST：当作新的 ESC 序列重新开始
    }
}
