namespace moreshell.Vt;

/// <summary>
/// 解码 + 应用 + 缓冲 的一体化流水线。
///
/// 每个输出流（stdout / stderr）各持一份，因为它们的编码判定必须互相独立：
/// 同一条命令里 git 可能输出 UTF-8 而内建命令输出 GBK。
/// </summary>
public sealed class VtPipeline
{
    private readonly VtDecoder _decoder = new();
    private readonly VtApplier _applier;

    public VtPipeline(int maxLines = 5000)
    {
        var doc = new TerminalDocument(maxLines);
        _applier = new VtApplier(doc);
        Document = doc;
    }

    public TerminalDocument Document { get; }

    /// <summary>
    /// 额外的动作消费者。交互模式把它指向实时写屏器，
    /// 这样同一份解析结果既能进缓冲区（供事后分析/转储），又能立刻画出来。
    /// </summary>
    public Action<VtAction>? Sink { get; set; }

    /// <summary>喂入一段已解码文本。转义序列可跨调用拆分，由解码器自己维护状态。</summary>
    public void Push(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (Sink is null)
        {
            _decoder.Decode(text, _applier.Apply);
            return;
        }

        var sink = Sink;
        _decoder.Decode(text, action =>
        {
            _applier.Apply(action);
            sink(action);
        });
    }

    public void Reset()
    {
        _decoder.Reset();
        Document.Reset();
    }
}
