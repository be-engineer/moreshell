namespace moreshell.Vt;

public enum VtActionKind
{
    /// <summary>可打印文本（已按 Rune 切分，可能跨多次 Decode 才凑齐一个转义序列）。</summary>
    Print,
    /// <summary>C0/C1 控制字符（\n \r \b \t BEL 等），Code 为码点。</summary>
    Execute,
    /// <summary>CSI 序列：ESC [ params intermediates final</summary>
    CsiDispatch,
    /// <summary>非 CSI 的 ESC 序列：ESC final</summary>
    EscDispatch,
    /// <summary>OSC 序列：ESC ] code ; text ST/BEL</summary>
    OscDispatch,
}

/// <summary>
/// 解析器产出的动作。刻意与“屏幕模型”解耦：
/// 同一份动作流，L2 喂给 <see cref="TerminalDocument"/>（行式日志），
/// L3/L4 可直接喂给全屏网格，无需改动解析器。
/// </summary>
public readonly struct VtAction
{
    /// <summary>
    /// 参数值中借位存放“该参数以冒号结束”的标记。
    /// ECMA-48 里 <c>38:2:&lt;cs&gt;:&lt;r&gt;:&lt;g&gt;:&lt;b&gt;</c>（冒号子参数，带 colorSpaceId）
    /// 与 <c>38;2;r;g;b</c>（分号参数，无 colorSpaceId）含义不同，
    /// 扁平化成 int[] 后必须保留这个区别，否则真彩色会取错分量。
    /// 参数被限制在 65535 以内，因此第16位是空闲的。
    /// </summary>
    public const int ColonSeparator = 0x10000;
    public const int ValueMask = 0xFFFF;

    /// <summary>取参数真实值（去掉分隔符标记）。</summary>
    public static int Value(int param) => param & ValueMask;

    /// <summary>该参数是否以冒号结束。</summary>
    public static bool EndsWithColon(int param) => (param & ColonSeparator) != 0;

    public VtActionKind Kind { get; init; }
    /// <summary>Print / OscDispatch 的文本。</summary>
    public string? Text { get; init; }
    /// <summary>Execute / EscDispatch 的码点或终止字符。</summary>
    public int Code { get; init; }
    /// <summary>CsiDispatch 的参数数组（缺省项为 -1）。可能为 null。</summary>
    public int[]? Params { get; init; }
    /// <summary>CsiDispatch 的终止字符，如 'm'、'K'、'J'。</summary>
    public char Final { get; init; }
    /// <summary>CsiDispatch 的私有前缀，如 '?'（DECSET/DECRST）。无则为 '\0'。</summary>
    public char Private { get; init; }
    /// <summary>CSI 的中间字节（0x20-0x2F），如 ' '。无则为 '\0'。</summary>
    public char Intermediate { get; init; }

    public static VtAction Print(string text) => new() { Kind = VtActionKind.Print, Text = text };
    public static VtAction Execute(int code) => new() { Kind = VtActionKind.Execute, Code = code };

    public override string ToString() => Kind switch
    {
        VtActionKind.Print => $"Print({Text})",
        VtActionKind.Execute => $"Execute(U+{Code:X4})",
        VtActionKind.CsiDispatch => $"Csi({Private}{string.Join(";", Params ?? Array.Empty<int>())}{Intermediate}{Final})",
        VtActionKind.EscDispatch => $"Esc({(char)Code})",
        VtActionKind.OscDispatch => $"Osc({Code}, {Text})",
        _ => Kind.ToString(),
    };
}
