using System.Text;

namespace moreshell.Runner;

/// <summary>
/// 输出编码嗅探器。
///
/// 背景（实测结论，见 docs/encoding-probe）：中文 Windows 上四个 shell 通过**管道**输出时
/// 并不统一——
///   cmd          → 始终 OEM 码页（936/GBK），<c>chcp 65001</c> 对管道输出**无效**
///   PowerShell   → 默认 GBK；前置 <c>[Console]::OutputEncoding=UTF8</c> 后变 UTF-8
///   Git Bash/WSL → UTF-8
///
/// 因此每个 shell 有一个 <b>fallback 编码</b>。但仅靠 per-shell 默认仍不够：
/// 在 cmd 下跑 node/python 这类程序时它们可能输出 UTF-8。
///
/// 所以采用“先验证再决定”的策略：
/// <list type="bullet">
///   <item>遇到非法 UTF-8 字节 → 立即判定为 fallback（GBK 文本几乎立刻触发）</item>
///   <item>累计一段长度内全是合法 UTF-8 且含多字节 → 判定为 UTF-8</item>
///   <item>纯 ASCII → 两种编码等价，直接放行，不做判定（零延迟）</item>
///   <item>进程结束仍未判定 → 用已有证据或 fallback 收尾</item>
/// </list>
///
/// 判定一旦做出就不再更改，后续字节走同一个 <see cref="Decoder"/> 流式解码。
/// </summary>
public sealed class OutputSniffer
{
    private enum Verdict
    {
        /// <summary>尾部有不完整的多字节序列，继续等更多字节。</summary>
        Incomplete,
        /// <summary>目前全是 ASCII，无需判定。</summary>
        AsciiOnly,
        /// <summary>整段是合法 UTF-8 且含多字节字符。</summary>
        Utf8,
        /// <summary>存在非法 UTF-8 字节。</summary>
        NotUtf8,
    }

    /// <summary>判定 UTF-8 所需的“连续合法字节”长度。GBK 中文在此长度内几乎必然出现非法字节。</summary>
    private const int ConfidenceBytes = 64;

    /// <summary>兜底：万一序列迟迟不完整，最多攒这么多字节。</summary>
    private const int MaxHold = 1 << 16;

    private readonly Encoding _fallback;
    private readonly MemoryStream _held = new();
    private Decoder? _decoder;
    private Encoding? _chosen;

    public OutputSniffer(Encoding fallback)
        => _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    /// <summary>已确定的编码；尚未判定时为 null。</summary>
    public Encoding? Chosen => _chosen;

    public bool IsDecided => _chosen != null;

    /// <summary>喂入一段原始字节，按需产出已解码文本。</summary>
    public void Push(byte[] buffer, int count, Action<string> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        if (count <= 0 && _chosen != null) return;

        if (_chosen != null)
        {
            if (count > 0) Stream(buffer, count, emit);
            return;
        }

        if (count > 0) _held.Write(buffer, 0, count);
        Evaluate(emit, atEof: false);
    }

    /// <summary>进程结束时调用，冲刷残余并强制做出最终判定。</summary>
    public void Finish(Action<string> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        if (_chosen == null) Evaluate(emit, atEof: true);
        _chosen ??= _fallback;   // 空输入兜底
    }

    // ---------------- 判定 ----------------

    private void Evaluate(Action<string> emit, bool atEof)
    {
        int len = (int)_held.Length;
        if (len == 0)
        {
            if (atEof) _chosen = _fallback;
            return;
        }

        var buffer = _held.GetBuffer();
        var verdict = Scan(buffer, len, atEof);

        switch (verdict)
        {
            case Verdict.AsciiOnly:
                // ASCII 在两种编码下完全等价，直接放行，避免累积延迟
                emit(Encoding.ASCII.GetString(buffer, 0, len));
                _held.SetLength(0);
                if (atEof) _chosen = _fallback;
                return;

            case Verdict.Incomplete:
                if (len >= MaxHold) { Decide(_fallback); Flush(emit); }
                return;

            case Verdict.NotUtf8:
                Decide(_fallback);
                Flush(emit);
                return;

            default: // Verdict.Utf8
                if (len >= ConfidenceBytes || atEof)
                {
                    Decide(Encoding.UTF8);
                    Flush(emit);
                }
                return;
        }
    }

    private void Decide(Encoding encoding)
    {
        _chosen = encoding;
        _decoder = encoding.GetDecoder();
    }

    private void Flush(Action<string> emit)
    {
        int len = (int)_held.Length;
        if (len > 0)
        {
            var buffer = _held.GetBuffer();
            var chars = new char[_chosen!.GetMaxCharCount(len)];
            int n = _decoder!.GetChars(buffer, 0, len, chars, 0, flush: true);
            if (n > 0) emit(new string(chars, 0, n));
            _held.SetLength(0);
        }
        // 重置解码器，避免 flush=true 后残留状态影响后续分块
        _decoder = _chosen!.GetDecoder();
    }

    private void Stream(byte[] buffer, int count, Action<string> emit)
    {
        var chars = new char[_chosen!.GetMaxCharCount(count)];
        int n = _decoder!.GetChars(buffer, 0, count, chars, 0, flush: false);
        if (n > 0) emit(new string(chars, 0, n));
    }

    // ---------------- UTF-8 结构校验 ----------------

    private static Verdict Scan(byte[] b, int len, bool atEof)
    {
        bool hasMultiByte = false;
        int i = 0;

        while (i < len)
        {
            byte c = b[i];

            if (c < 0x80) { i++; continue; }

            int need;
            if (c >= 0xC2 && c <= 0xDF) need = 1;
            else if (c >= 0xE0 && c <= 0xEF) need = 2;
            else if (c >= 0xF0 && c <= 0xF4) need = 3;
            else return Verdict.NotUtf8;          // 0x80-0xC1 与 0xF5-0xFF 都不是合法首字节

            if (i + need >= len)
            {
                // 序列被截断
                return atEof ? Verdict.NotUtf8 : Verdict.Incomplete;
            }

            for (int k = 1; k <= need; k++)
                if ((b[i + k] & 0xC0) != 0x80) return Verdict.NotUtf8;

            // overlong 编码
            if (need == 2 && c == 0xE0 && b[i + 1] < 0xA0) return Verdict.NotUtf8;
            if (need == 3 && c == 0xF0 && b[i + 1] < 0x90) return Verdict.NotUtf8;
            // UTF-16 代理区 / 超出 U+10FFFF
            if (need == 2 && c == 0xED && b[i + 1] >= 0xA0) return Verdict.NotUtf8;
            if (need == 3 && c == 0xF4 && b[i + 1] >= 0x90) return Verdict.NotUtf8;

            hasMultiByte = true;
            i += need + 1;
        }

        return hasMultiByte ? Verdict.Utf8 : Verdict.AsciiOnly;
    }
}
