namespace moreshell.Vt;

/// <summary>
/// SGR（Select Graphic Rendition，即 <c>ESC[…m</c>）参数解析。
///
/// 抽成独立静态类，是为了让两条渲染路径共用同一份逻辑：
/// <list type="bullet">
///   <item>L2 行式缓冲：<see cref="VtApplier"/> 把结果写进 <see cref="TerminalDocument.CurrentStyle"/></item>
///   <item>交互实时渲染：<c>LiveWriter</c> 直接持有当前样式</item>
/// </list>
/// 两条路径必须逐字节一致，否则"测试过的"和"用户看到的"会是两回事。
/// </summary>
public static class Sgr
{
    public static Style Apply(Style current, int[] p)
    {
        if (p.Length == 0) return Style.Default;

        var st = current;
        int i = 0;
        while (i < p.Length)
        {
            int v = VtAction.Value(p[i]);
            i = ApplyOne(v, p, i, ref st) + 1;
        }
        return st;
    }

    /// <summary>应用一条 SGR，返回它「最后消费的参数下标」（调用方再 +1）。</summary>
    private static int ApplyOne(int v, int[] p, int i, ref Style st)
    {
        switch (v)
        {
            case 0: st = Style.Default; return i;

            case 1: st = st.WithFlag(StyleFlags.Bold, true); return i;
            case 2: st = st.WithFlag(StyleFlags.Dim, true); return i;
            case 3: st = st.WithFlag(StyleFlags.Italic, true); return i;
            case 4: st = st.WithFlag(StyleFlags.Underline, true); return i;
            case 5: case 6: st = st.WithFlag(StyleFlags.Blink, true); return i;
            case 7: st = st.WithFlag(StyleFlags.Inverse, true); return i;
            case 8: st = st.WithFlag(StyleFlags.Hidden, true); return i;
            case 9: st = st.WithFlag(StyleFlags.Strike, true); return i;

            case 21: case 22:
                st = st.WithFlag(StyleFlags.Bold, false).WithFlag(StyleFlags.Dim, false);
                return i;
            case 23: st = st.WithFlag(StyleFlags.Italic, false); return i;
            case 24: st = st.WithFlag(StyleFlags.Underline, false); return i;
            case 25: st = st.WithFlag(StyleFlags.Blink, false); return i;
            case 27: st = st.WithFlag(StyleFlags.Inverse, false); return i;
            case 28: st = st.WithFlag(StyleFlags.Hidden, false); return i;
            case 29: st = st.WithFlag(StyleFlags.Strike, false); return i;

            case >= 30 and <= 37: st = st.WithFg(v - 30); return i;
            case 38: return ExtendedColor(p, i, isFg: true, ref st);
            case 39: st = st.WithFg(Colors.Default); return i;
            case >= 40 and <= 47: st = st.WithBg(v - 40); return i;
            case 48: return ExtendedColor(p, i, isFg: false, ref st);
            case 49: st = st.WithBg(Colors.Default); return i;

            case >= 90 and <= 97: st = st.WithFg(v - 90 + 8); return i;
            case >= 100 and <= 107: st = st.WithBg(v - 100 + 8); return i;

            default: return i;   // 未知码位安全忽略
        }
    }

    /// <summary>
    /// 解析 38/48 扩展色，返回最后消费的参数下标。覆盖五种常见形态：
    /// <code>
    ///   38;5;n           分号 256 色
    ///   38:5:n           冒号 256 色
    ///   38;2;r;g;b       分号真彩色（无 colorSpaceId）
    ///   38:2:cs:r:g:b    冒号真彩色（带 colorSpaceId，共 6 项）
    ///   38:2:r:g:b       冒号真彩色（无 colorSpaceId，共 5 项）
    /// </code>
    /// </summary>
    private static int ExtendedColor(int[] p, int i, bool isFg, ref Style st)
    {
        if (i + 1 >= p.Length) return i;
        int mode = VtAction.Value(p[i + 1]);

        if (mode == 5)
        {
            if (i + 2 >= p.Length) return i + 1;
            int idx = Math.Clamp(VtAction.Value(p[i + 2]), 0, 255);
            st = isFg ? st.WithFg(idx) : st.WithBg(idx);
            return i + 2;
        }

        if (mode == 2)
        {
            int m = i + 1;
            int baseIdx = m + 1 + (HasColorSpaceId(p, m) ? 1 : 0);
            if (baseIdx + 2 >= p.Length) return i + 1;

            int r = Math.Clamp(VtAction.Value(p[baseIdx]), 0, 255);
            int g = Math.Clamp(VtAction.Value(p[baseIdx + 1]), 0, 255);
            int b = Math.Clamp(VtAction.Value(p[baseIdx + 2]), 0, 255);
            int rgb = Colors.Rgb(r, g, b);
            st = isFg ? st.WithFg(rgb) : st.WithBg(rgb);
            return baseIdx + 2;
        }

        return i + 1;
    }

    /// <summary>
    /// 判断 <c>38:2:</c> 形态是否多带一个 colorSpaceId 分量。
    /// 依据：mode 之后紧跟的两个分隔符都必须是冒号，且剩余值数量够 4 个。
    /// 这样能正确区分 <c>38:2:0:255:0:0</c>（带 cs）与 <c>38:2:255;0;0;1</c>（不带，后随其他 SGR）。
    /// </summary>
    private static bool HasColorSpaceId(int[] p, int m)
        => p.Length - (m + 1) >= 4
           && VtAction.EndsWithColon(p[m])
           && VtAction.EndsWithColon(p[m + 1]);
}
