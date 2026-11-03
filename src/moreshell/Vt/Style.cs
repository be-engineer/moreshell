namespace moreshell.Vt;

/// <summary>
/// 颜色编码约定（用 int 承载三种形态，避免引入过多类型）：
///   -1          = 终端默认色
///   0..255      = 256 色调色板索引
///   >=0x1000000 = 真彩色，减去 <see cref="Colors.TrueColorBase"/> 得 0xRRGGBB
/// </summary>
public static class Colors
{
    public const int Default = -1;
    public const int TrueColorBase = 0x1000000;

    public static int Rgb(int r, int g, int b) => TrueColorBase | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
    public static bool IsDefault(int c) => c < 0;
    public static bool IsTrueColor(int c) => c >= TrueColorBase;
}

[Flags]
public enum StyleFlags
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strike = 128,
}

/// <summary>一段文本的显示样式。不可变，可安全共享。</summary>
public readonly record struct Style(StyleFlags Flags, int Fg, int Bg)
{
    public static readonly Style Default = new(StyleFlags.None, Colors.Default, Colors.Default);

    public bool Has(StyleFlags f) => (Flags & f) != 0;

    public Style WithFlag(StyleFlags f, bool on)
        => new(on ? Flags | f : Flags & ~f, Fg, Bg);

    public Style WithFg(int fg) => new(Flags, fg, Bg);
    public Style WithBg(int bg) => new(Flags, Fg, bg);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Has(StyleFlags.Bold)) parts.Add("bold");
        if (Has(StyleFlags.Dim)) parts.Add("dim");
        if (Has(StyleFlags.Italic)) parts.Add("italic");
        if (Has(StyleFlags.Underline)) parts.Add("underline");
        if (Has(StyleFlags.Inverse)) parts.Add("inverse");
        if (Has(StyleFlags.Strike)) parts.Add("strike");
        if (!Colors.IsDefault(Fg)) parts.Add("fg=" + Fg);
        if (!Colors.IsDefault(Bg)) parts.Add("bg=" + Bg);
        return parts.Count == 0 ? "default" : string.Join(",", parts);
    }
}
