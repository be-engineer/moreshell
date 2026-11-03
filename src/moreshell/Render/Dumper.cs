using System.Text.Json;
using moreshell.Vt;

namespace moreshell.Render;

/// <summary>
/// 把解析结果序列化成 JSON，供自动化验收断言使用。
///
/// 交互模式用不到它。它的价值在于：让「中文有没有乱码」「颜色有没有解析对」
/// 变成**可逐字节验证的输出**，而不是靠人眼在控制台里看。
/// 控制台是 CP936、还会受重定向影响，只有 JSON 里的文本与色号是确定的。
/// </summary>
public static class Dumper
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Dump(object payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>一行 → { text, segments[] }。</summary>
    public static object DescribeLine(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var cells = line.View;
        return new Dictionary<string, object?>
        {
            ["text"] = string.Concat(cells.Select(c => c.Text)),
            ["segments"] = cells
                .Select(c => new Dictionary<string, object?>
                {
                    ["t"] = c.Text,
                    ["fg"] = ColorToken(c.Style.Fg),
                    ["bg"] = ColorToken(c.Style.Bg),
                    ["flags"] = FlagTokens(c.Style.Flags),
                })
                .ToArray(),
        };
    }

    public static object DescribeDocument(TerminalDocument doc)
        => doc.Flush().Select(DescribeLine).ToArray();

    /// <summary>
    /// 颜色 → 明确的字符串标签，杜绝"数值相等但语义不同"的歧义：
    /// <c>default</c> / <c>ansi-0..15</c> / <c>256-N</c> / <c>rgb-RRGGBB</c>。
    /// </summary>
    public static string ColorToken(int color)
    {
        if (Colors.IsDefault(color)) return "default";

        if (Colors.IsTrueColor(color))
        {
            int v = color - Colors.TrueColorBase;
            return $"rgb-{v:X6}";
        }

        return color < 16 ? $"ansi-{color}" : $"256-{color}";
    }

    public static string[] FlagTokens(StyleFlags flags)
    {
        var list = new List<string>(8);
        if ((flags & StyleFlags.Bold) != 0) list.Add("bold");
        if ((flags & StyleFlags.Dim) != 0) list.Add("dim");
        if ((flags & StyleFlags.Italic) != 0) list.Add("italic");
        if ((flags & StyleFlags.Underline) != 0) list.Add("underline");
        if ((flags & StyleFlags.Blink) != 0) list.Add("blink");
        if ((flags & StyleFlags.Inverse) != 0) list.Add("inverse");
        if ((flags & StyleFlags.Hidden) != 0) list.Add("hidden");
        if ((flags & StyleFlags.Strike) != 0) list.Add("strike");
        return list.ToArray();
    }
}
