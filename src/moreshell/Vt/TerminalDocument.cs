using System.Text;

namespace moreshell.Vt;

/// <summary>一个显示单元：文本（可能是代理对/宽字符）+ 样式。</summary>
public readonly record struct Cell(string Text, Style Style)
{
    public static readonly Cell Space = new(" ", Style.Default);
}

/// <summary>一行文本。用 List 模拟可随机写入的列，以支持 \r 覆写。</summary>
public sealed class StyledLine
{
    internal readonly List<Cell> Cells = new();

    public int Length => Cells.Count;
    public IReadOnlyList<Cell> View => Cells;

    public Cell? this[int index] => index >= 0 && index < Cells.Count ? Cells[index] : null;

    /// <summary>整行的“本质样式”——若全行同色则返回，否则为 null（用于压缩渲染）。</summary>
    public bool IsUniform(out Style style)
    {
        if (Cells.Count == 0) { style = Style.Default; return true; }
        style = Cells[0].Style;
        for (int i = 1; i < Cells.Count; i++)
            if (Cells[i].Style != style) return false;
        return true;
    }
}

/// <summary>
/// 行式终端缓冲区。
/// 与真终端的差异（L2 已知取舍）：不维护固定高度的“屏幕”，而是维护一份可滚动的日志，
/// 已提交的行在上方，正在写入的行在下方。这样能覆盖一次性命令输出的全部常见形态，
/// 但不支持全屏程序（vim/top）所需的屏幕寻址。
/// </summary>
public sealed class TerminalDocument
{
    private readonly List<StyledLine> _lines = new();
    private readonly int _maxLines;
    private StyledLine _current = new();
    private int _col;

    public TerminalDocument(int maxLines = 5000)
    {
        _maxLines = Math.Max(100, maxLines);
    }

    /// <summary>已提交（可滚动查看）的行。</summary>
    public IReadOnlyList<StyledLine> Lines => _lines;

    /// <summary>正在写入的最后一行。</summary>
    public StyledLine CurrentLine => _current;

    public int CurrentColumn => _col;
    public Style CurrentStyle { get; set; } = Style.Default;

    public int LineCount => _lines.Count + (_current.Length > 0 || _col > 0 ? 1 : 0);

    public void Reset()
    {
        _lines.Clear();
        _current = new StyledLine();
        _col = 0;
        CurrentStyle = Style.Default;
    }

    // ---------- 基本输出 ----------

    public void Put(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var rune in EnumerateRunes(text))
            PutRune(rune);
    }

    public void PutRune(Rune rune)
    {
        SetCell(_col, new Cell(rune.ToString(), CurrentStyle));
        _col++;
    }

    private void SetCell(int col, Cell cell)
    {
        if (col < 0) return;
        var cells = _current.Cells;
        while (cells.Count < col)
            cells.Add(Cell.Space);
        if (col < cells.Count) cells[col] = cell;
        else cells.Add(cell);
    }

    // ---------- 控制字符 ----------

    public void CarriageReturn() => _col = 0;

    public void LineFeed()
    {
        CommitCurrent();
        _current = new StyledLine();
        _col = 0;
    }

    public void Backspace() => _col = Math.Max(0, _col - 1);

    public void Tab() => _col = (_col / 8 + 1) * 8;

    // ---------- 擦除 ----------

    /// <summary>EL：0=从光标到行尾，1=从行首到光标，2=整行。</summary>
    public void EraseInLine(int mode)
    {
        var cells = _current.Cells;
        switch (mode)
        {
            case 0:
                if (_col < cells.Count) cells.RemoveRange(_col, cells.Count - _col);
                break;
            case 1:
                // 从行首到光标（含光标位置），与 xterm 等终端一致
                for (int i = 0; i <= _col && i < cells.Count; i++)
                    cells[i] = Cell.Space;
                break;
            default:
                cells.Clear();
                _col = 0;
                break;
        }
    }

    /// <summary>ED：0=光标以下，1=光标以上，2/3=全部。</summary>
    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                break;
            case 1:
                EraseInLine(1);
                _lines.Clear();
                break;
            default:
                _lines.Clear();
                _current = new StyledLine();
                _col = 0;
                break;
        }
    }

    public void Clear()
    {
        _lines.Clear();
        _current = new StyledLine();
        _col = 0;
    }

    // ---------- 光标列移动（不支持行寻址，见类注释） ----------

    public void SetColumn(int col) => _col = Math.Max(0, col);

    public void MoveColumn(int delta) => _col = Math.Max(0, _col + delta);

    // ---------- 提交 ----------

    public void CommitCurrent()
    {
        if (_current.Length == 0 && _col == 0) return;
        _lines.Add(_current);
        if (_lines.Count > _maxLines)
            _lines.RemoveRange(0, _lines.Count - _maxLines);
    }

    /// <summary>结束时把正在写入的行并入结果。</summary>
    public IReadOnlyList<StyledLine> Flush()
    {
        CommitCurrent();
        _current = new StyledLine();
        _col = 0;
        return _lines;
    }

    private static IEnumerable<Rune> EnumerateRunes(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (Rune.TryGetRuneAt(s, i, out var rune))
            {
                yield return rune;
                i += rune.Utf16SequenceLength;
            }
            else
            {
                // 孤立代理项：替换为 U+FFFD，避免渲染层崩溃
                yield return Rune.ReplacementChar;
                i++;
            }
        }
    }
}
