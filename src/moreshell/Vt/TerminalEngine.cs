using System.Text;

namespace moreshell.Vt;

/// <summary>
/// 把 PTY 的字节流接到 <see cref="ScreenBuffer"/> 上。
///
/// 两处“有状态”是必须的，缺一不可：
/// <list type="bullet">
///   <item>UTF-8 <see cref="Decoder"/>：多字节字符可能被管道切成两半；</item>
///   <item><see cref="VtDecoder"/>：转义序列同样可能跨读块。</item>
/// </list>
/// 因此每次 <see cref="Feed(ReadOnlySpan{byte})"/> 都不假设输入是完整消息。
/// </summary>
public sealed class TerminalEngine
{
    private readonly ScreenBuffer _screen;
    private readonly ScreenApplier _applier;
    private readonly VtDecoder _decoder = new();
    private readonly Decoder _utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();
    private readonly Action<VtAction> _apply;

    public TerminalEngine(ScreenBuffer screen, Action<string>? respond = null)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _applier = new ScreenApplier(screen) { Respond = respond };
        _apply = _applier.Apply;
    }

    public ScreenBuffer Screen => _screen;

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        // UTF-8 解码后字符数不会超过字节数（非法序列按 U+FFFD 每个字节一个）
        if (data.Length <= 2048)
        {
            Span<char> chars = stackalloc char[data.Length];
            DecodeInto(data, chars);
        }
        else
        {
            DecodeInto(data, new char[data.Length]);
        }
    }

    private void DecodeInto(ReadOnlySpan<byte> data, Span<char> chars)
    {
        int written = _utf8.GetChars(data, chars, flush: false);
        if (written > 0)
            _decoder.Decode(new string(chars[..written]), _apply);
    }

    /// <summary>直接喂入文本（测试与 OSC 应答等场景）。</summary>
    public void Feed(string text) => _decoder.Decode(text, _apply);

    public void Reset()
    {
        _decoder.Reset();
        _utf8.Reset();
    }
}
