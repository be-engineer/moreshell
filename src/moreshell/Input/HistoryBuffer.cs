namespace moreshell.Input;

/// <summary>
/// 行式 REPL 的内存命令历史。只保留当前进程内的记录，不落盘。
/// 空行和与上一条完全相同的命令会被忽略。
/// </summary>
public sealed class HistoryBuffer
{
    public const int DefaultCapacity = 500;

    private readonly List<string> _items = new();
    private readonly int _capacity;
    private int _index;
    private string _draft = "";

    public HistoryBuffer(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _items.Count;

    public IReadOnlyList<string> Items => _items;

    /// <summary>开始读取新的一行：历史游标回到末尾，丢弃上次未提交的草稿。</summary>
    public void BeginEdit()
    {
        _index = _items.Count;
        _draft = "";
    }

    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_items.Count > 0 && string.Equals(_items[^1], line, StringComparison.Ordinal)) return;

        _items.Add(line);
        if (_items.Count > _capacity)
            _items.RemoveRange(0, _items.Count - _capacity);

        _index = _items.Count;
        _draft = "";
    }

    /// <summary>↑：返回上一条；已在最旧一条时返回 null。首次离开草稿时保存 current。</summary>
    public string? Previous(string current)
    {
        if (_items.Count == 0 || _index == 0) return null;
        if (_index == _items.Count) _draft = current;
        _index--;
        return _items[_index];
    }

    /// <summary>↓：返回下一条；越过最新一条后回到编辑前的草稿。</summary>
    public string? Next()
    {
        if (_items.Count == 0 || _index >= _items.Count) return null;
        _index++;
        return _index == _items.Count ? _draft : _items[_index];
    }
}
