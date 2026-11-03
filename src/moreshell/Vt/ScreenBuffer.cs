using System.Text;

namespace moreshell.Vt;

/// <summary>
/// L3 全屏网格。与 L2 的 <see cref="TerminalDocument"/> 不同，它维护固定行列的二维屏幕、
/// 光标坐标、滚动区域和备用屏，因此能承载 vim/top/less 这类全屏程序。
///
/// 坐标一律 0 基：<see cref="CursorRow"/> / <see cref="CursorColumn"/>。
/// </summary>
public sealed class ScreenBuffer
{
    private sealed class Grid
    {
        public Cell[,] Cells;
        public int Row;
        public int Column;
        public Style Style = Style.Default;
        public bool WrapPending;
        public int ScrollTop;
        public int ScrollBottom;
        public int SavedRow;
        public int SavedColumn;
        public Style SavedStyle = Style.Default;
        public bool SavedWrapPending;

        public Grid(int rows, int columns)
        {
            Cells = Blank(rows, columns);
            ScrollBottom = rows - 1;
        }

        public static Cell[,] Blank(int rows, int columns)
        {
            var cells = new Cell[rows, columns];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    cells[r, c] = Cell.Space;
            return cells;
        }

        public void Clear()
        {
            for (int r = 0; r < Cells.GetLength(0); r++)
                for (int c = 0; c < Cells.GetLength(1); c++)
                    Cells[r, c] = Cell.Space;
        }
    }

    private readonly int _maxScrollback;
    private readonly List<StyledLine> _scrollback = new();
    private Grid _primary;
    private Grid _alternate;
    private bool _alternateActive;

    public ScreenBuffer(int columns = 80, int rows = 24, int maxScrollback = 5000)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows < 1) throw new ArgumentOutOfRangeException(nameof(rows));

        Columns = columns;
        Rows = rows;
        _maxScrollback = Math.Max(0, maxScrollback);
        _primary = new Grid(rows, columns);
        _alternate = new Grid(rows, columns);
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    public int CursorRow => Active.Row;
    public int CursorColumn => Active.Column;
    public Style CurrentStyle
    {
        get => Active.Style;
        set => Active.Style = value;
    }

    public bool CursorVisible { get; set; } = true;
    public bool AutoWrap { get; set; } = true;
    public bool InsertMode { get; set; }
    public bool BracketedPaste { get; set; }
    public bool AlternateScreenActive => _alternateActive;
    public string? Title { get; private set; }

    /// <summary>自上次 <see cref="ClearDirty"/> 后是否发生过修改。</summary>
    public bool IsDirty { get; private set; }

    public IReadOnlyList<StyledLine> Scrollback => _scrollback;

    private Grid Active => _alternateActive ? _alternate : _primary;

    public Cell GetCell(int row, int column) => Active.Cells[row, column];

    public void ClearDirty() => IsDirty = false;

    private void MarkDirty() => IsDirty = true;

    public void SetTitle(string? title) => Title = title;

    // ------------------------------------------------------------------
    // 尺寸
    // ------------------------------------------------------------------

    public void Resize(int columns, int rows)
    {
        if (columns < 1 || rows < 1) return;
        if (columns == Columns && rows == Rows) return;

        Columns = columns;
        Rows = rows;

        _primary = ResizeGrid(_primary, rows, columns);
        _alternate = ResizeGrid(_alternate, rows, columns);
        MarkDirty();
    }

    private static Grid ResizeGrid(Grid old, int rows, int columns)
    {
        var resized = new Grid(rows, columns);
        int copyRows = Math.Min(rows, old.Cells.GetLength(0));
        int copyCols = Math.Min(columns, old.Cells.GetLength(1));
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                resized.Cells[r, c] = old.Cells[r, c];

        resized.Row = Math.Clamp(old.Row, 0, rows - 1);
        resized.Column = Math.Clamp(old.Column, 0, columns - 1);
        resized.Style = old.Style;
        resized.WrapPending = old.WrapPending;
        resized.ScrollTop = 0;
        resized.ScrollBottom = rows - 1;
        resized.SavedRow = Math.Clamp(old.SavedRow, 0, rows - 1);
        resized.SavedColumn = Math.Clamp(old.SavedColumn, 0, columns - 1);
        resized.SavedStyle = old.SavedStyle;
        resized.SavedWrapPending = old.SavedWrapPending;
        return resized;
    }

    // ------------------------------------------------------------------
    // 输出
    // ------------------------------------------------------------------

    public void Put(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var rune in text.EnumerateRunes())
            PutRune(rune);
    }

    public void PutRune(Rune rune)
    {
        var grid = Active;
        int width = RuneWidth(rune);

        if (width == 0)
        {
            AppendCombining(rune);
            return;
        }

        if (grid.WrapPending)
        {
            grid.WrapPending = false;
            if (AutoWrap) LineFeedCore(returnToColumnZero: true);
        }

        // 双宽字符放不进最后一列：先换行，否则会破坏网格列对齐
        if (width == 2 && grid.Column >= Columns - 1)
        {
            if (AutoWrap && Columns > 1) LineFeedCore(returnToColumnZero: true);
            else grid.Column = Math.Max(0, Columns - 1);
        }

        if (InsertMode) InsertChars(width);

        grid.Cells[grid.Row, grid.Column] = new Cell(rune.ToString(), grid.Style);
        if (width == 2 && grid.Column + 1 < Columns)
            grid.Cells[grid.Row, grid.Column + 1] = new Cell(string.Empty, grid.Style);

        grid.Column += width;
        if (grid.Column >= Columns)
        {
            grid.Column = Columns - 1;
            grid.WrapPending = AutoWrap;
        }

        MarkDirty();
    }

    /// <summary>组合字符（宽度 0）并入前一个显示单元，避免产生孤儿列。</summary>
    private void AppendCombining(Rune rune)
    {
        var grid = Active;
        int index = grid.Column > 0 ? grid.Column - 1 : 0;
        if (index < 0 || index >= Columns) return;

        var cell = grid.Cells[grid.Row, index];
        if (cell.Text.Length == 0 && index > 0)
        {
            index--;
            cell = grid.Cells[grid.Row, index];
        }

        grid.Cells[grid.Row, index] = cell with { Text = cell.Text + rune };
        MarkDirty();
    }

    // ------------------------------------------------------------------
    // C0 控制字符
    // ------------------------------------------------------------------

    public void CarriageReturn()
    {
        Active.Column = 0;
        Active.WrapPending = false;
        MarkDirty();
    }

    public void LineFeed()
    {
        LineFeedCore(returnToColumnZero: false);
        MarkDirty();
    }

    private void LineFeedCore(bool returnToColumnZero)
    {
        var grid = Active;
        grid.WrapPending = false;
        if (returnToColumnZero) grid.Column = 0;

        if (grid.Row == grid.ScrollBottom) ScrollUp(1);
        else if (grid.Row < Rows - 1) grid.Row++;
    }

    public void ReverseIndex()
    {
        var grid = Active;
        grid.WrapPending = false;
        if (grid.Row == grid.ScrollTop) ScrollDown(1);
        else if (grid.Row > 0) grid.Row--;
        MarkDirty();
    }

    public void Backspace()
    {
        var grid = Active;
        if (grid.WrapPending) grid.WrapPending = false;
        else grid.Column = Math.Max(0, grid.Column - 1);
        MarkDirty();
    }

    public void Tab()
    {
        var grid = Active;
        int next = (grid.Column / 8 + 1) * 8;
        grid.Column = Math.Min(Columns - 1, next);
        grid.WrapPending = false;
        MarkDirty();
    }

    // ------------------------------------------------------------------
    // 光标
    // ------------------------------------------------------------------

    public void SetCursor(int row, int column)
    {
        var grid = Active;
        grid.Row = Math.Clamp(row, 0, Rows - 1);
        grid.Column = Math.Clamp(column, 0, Columns - 1);
        grid.WrapPending = false;
        MarkDirty();
    }

    public void MoveCursor(int deltaRow, int deltaColumn)
    {
        var grid = Active;
        grid.Row = Math.Clamp(grid.Row + deltaRow, grid.ScrollTop, grid.ScrollBottom);
        grid.Column = Math.Clamp(grid.Column + deltaColumn, 0, Columns - 1);
        grid.WrapPending = false;
        MarkDirty();
    }

    public void SetColumn(int column)
    {
        Active.Column = Math.Clamp(column, 0, Columns - 1);
        Active.WrapPending = false;
        MarkDirty();
    }

    public void SetRow(int row)
    {
        var grid = Active;
        grid.Row = Math.Clamp(row, grid.ScrollTop, grid.ScrollBottom);
        grid.WrapPending = false;
        MarkDirty();
    }

    public void SaveCursor()
    {
        var grid = Active;
        grid.SavedRow = grid.Row;
        grid.SavedColumn = grid.Column;
        grid.SavedStyle = grid.Style;
        grid.SavedWrapPending = grid.WrapPending;
        MarkDirty();
    }

    public void RestoreCursor()
    {
        var grid = Active;
        grid.Row = Math.Clamp(grid.SavedRow, 0, Rows - 1);
        grid.Column = Math.Clamp(grid.SavedColumn, 0, Columns - 1);
        grid.Style = grid.SavedStyle;
        grid.WrapPending = grid.SavedWrapPending;
        MarkDirty();
    }

    // ------------------------------------------------------------------
    // 擦除
    // ------------------------------------------------------------------

    /// <summary>EL：0=光标到行尾，1=行首到光标，2=整行。</summary>
    public void EraseInLine(int mode)
    {
        var grid = Active;
        switch (mode)
        {
            case 0:
                BlankRow(grid, grid.Row, grid.Column, Columns - 1);
                break;
            case 1:
                BlankRow(grid, grid.Row, 0, grid.Column);
                break;
            default:
                BlankRow(grid, grid.Row, 0, Columns - 1);
                break;
        }
        MarkDirty();
    }

    /// <summary>ED：0=光标以下，1=光标以上，2/3=全部；3 同时清空回滚。</summary>
    public void EraseInDisplay(int mode)
    {
        var grid = Active;
        switch (mode)
        {
            case 0:
                BlankRow(grid, grid.Row, grid.Column, Columns - 1);
                for (int r = grid.Row + 1; r < Rows; r++) BlankRow(grid, r, 0, Columns - 1);
                break;
            case 1:
                for (int r = 0; r < grid.Row; r++) BlankRow(grid, r, 0, Columns - 1);
                BlankRow(grid, grid.Row, 0, grid.Column);
                break;
            case 3:
                _scrollback.Clear();
                goto default;
            default:
                for (int r = 0; r < Rows; r++) BlankRow(grid, r, 0, Columns - 1);
                break;
        }
        MarkDirty();
    }

    public void EraseChars(int count)
    {
        var grid = Active;
        BlankRow(grid, grid.Row, grid.Column, Math.Min(Columns - 1, grid.Column + count - 1));
        MarkDirty();
    }

    private static void BlankRow(Grid grid, int row, int from, int to)
    {
        if (from < 0) from = 0;
        if (to >= grid.Cells.GetLength(1)) to = grid.Cells.GetLength(1) - 1;
        for (int c = from; c <= to; c++)
            grid.Cells[row, c] = Cell.Space;
    }

    public void Clear()
    {
        Active.Clear();
        Active.Row = 0;
        Active.Column = 0;
        Active.WrapPending = false;
        Active.ScrollTop = 0;
        Active.ScrollBottom = Rows - 1;
        MarkDirty();
    }

    /// <summary>RIS 完全重置。</summary>
    public void Reset()
    {
        _alternateActive = false;
        _scrollback.Clear();
        _primary = new Grid(Rows, Columns);
        _alternate = new Grid(Rows, Columns);
        CursorVisible = true;
        AutoWrap = true;
        InsertMode = false;
        BracketedPaste = false;
        Title = null;
        MarkDirty();
    }

    // ------------------------------------------------------------------
    // 滚动区域与滚动
    // ------------------------------------------------------------------

    public void SetScrollRegion(int top, int bottom)
    {
        if (top < 0) top = 0;
        if (bottom >= Rows) bottom = Rows - 1;
        if (bottom <= top) return;

        var grid = Active;
        grid.ScrollTop = top;
        grid.ScrollBottom = bottom;
        grid.Row = top;
        grid.Column = 0;
        grid.WrapPending = false;
        MarkDirty();
    }

    public void ResetScrollRegion()
    {
        var grid = Active;
        grid.ScrollTop = 0;
        grid.ScrollBottom = Rows - 1;
        MarkDirty();
    }

    public void ScrollUp(int count)
    {
        ScrollRegionUp(Active, count);
        MarkDirty();
    }

    public void ScrollDown(int count)
    {
        ScrollRegionDown(Active, count);
        MarkDirty();
    }

    private void ScrollRegionUp(Grid grid, int count)
    {
        int height = grid.ScrollBottom - grid.ScrollTop + 1;
        count = Math.Clamp(count, 0, height);
        if (count == 0) return;

        if (!_alternateActive && grid.ScrollTop == 0)
        {
            for (int i = 0; i < count; i++)
                AppendScrollback(grid, grid.ScrollTop + i);
        }

        for (int r = grid.ScrollTop; r <= grid.ScrollBottom - count; r++)
            for (int c = 0; c < Columns; c++)
                grid.Cells[r, c] = grid.Cells[r + count, c];

        for (int r = grid.ScrollBottom - count + 1; r <= grid.ScrollBottom; r++)
            for (int c = 0; c < Columns; c++)
                grid.Cells[r, c] = Cell.Space;
    }

    private void ScrollRegionDown(Grid grid, int count)
    {
        int height = grid.ScrollBottom - grid.ScrollTop + 1;
        count = Math.Clamp(count, 0, height);
        if (count == 0) return;

        for (int r = grid.ScrollBottom; r >= grid.ScrollTop + count; r--)
            for (int c = 0; c < Columns; c++)
                grid.Cells[r, c] = grid.Cells[r - count, c];

        for (int r = grid.ScrollTop; r < grid.ScrollTop + count; r++)
            for (int c = 0; c < Columns; c++)
                grid.Cells[r, c] = Cell.Space;
    }

    private void AppendScrollback(Grid grid, int row)
    {
        if (_maxScrollback == 0) return;

        var line = new StyledLine();
        int last = Columns - 1;
        while (last >= 0 && grid.Cells[row, last].Text == " " && grid.Cells[row, last].Style == Style.Default)
            last--;

        for (int c = 0; c <= last; c++)
            line.Cells.Add(grid.Cells[row, c]);

        _scrollback.Add(line);
        if (_scrollback.Count > _maxScrollback)
            _scrollback.RemoveRange(0, _scrollback.Count - _maxScrollback);
    }

    // ------------------------------------------------------------------
    // 行 / 字符插入删除
    // ------------------------------------------------------------------

    public void InsertLines(int count)
    {
        var grid = Active;
        if (grid.Row < grid.ScrollTop || grid.Row > grid.ScrollBottom) return;

        int height = grid.ScrollBottom - grid.Row + 1;
        count = Math.Clamp(count, 0, height);

        for (int r = grid.ScrollBottom; r >= grid.Row + count; r--)
            for (int c = 0; c < Columns; c++)
                grid.Cells[r, c] = grid.Cells[r - count, c];

        for (int r = grid.Row; r < grid.Row + count; r++)
            for (int c = 0; c < Columns; c++)
                grid.Cells[r, c] = Cell.Space;

        grid.Column = 0;
        grid.WrapPending = false;
        MarkDirty();
    }

    public void DeleteLines(int count)
    {
        var grid = Active;
        if (grid.Row < grid.ScrollTop || grid.Row > grid.ScrollBottom) return;

        int height = grid.ScrollBottom - grid.Row + 1;
        count = Math.Clamp(count, 0, height);

        for (int r = grid.Row; r <= grid.ScrollBottom - count; r++)
            for (int c = 0; c < Columns; c++)
                grid.Cells[r, c] = grid.Cells[r + count, c];

        for (int r = grid.ScrollBottom - count + 1; r <= grid.ScrollBottom; r++)
            for (int c = 0; c < Columns; c++)
                grid.Cells[r, c] = Cell.Space;

        grid.Column = 0;
        grid.WrapPending = false;
        MarkDirty();
    }

    public void InsertChars(int count)
    {
        var grid = Active;
        count = Math.Clamp(count, 0, Columns - grid.Column);
        if (count == 0) return;

        for (int c = Columns - 1; c >= grid.Column + count; c--)
            grid.Cells[grid.Row, c] = grid.Cells[grid.Row, c - count];

        for (int c = grid.Column; c < grid.Column + count; c++)
            grid.Cells[grid.Row, c] = Cell.Space;

        MarkDirty();
    }

    public void DeleteChars(int count)
    {
        var grid = Active;
        count = Math.Clamp(count, 0, Columns - grid.Column);
        if (count == 0) return;

        for (int c = grid.Column; c < Columns - count; c++)
            grid.Cells[grid.Row, c] = grid.Cells[grid.Row, c + count];

        for (int c = Columns - count; c < Columns; c++)
            grid.Cells[grid.Row, c] = Cell.Space;

        MarkDirty();
    }

    // ------------------------------------------------------------------
    // 备用屏
    // ------------------------------------------------------------------

    public void EnterAlternateScreen(bool clear)
    {
        if (_alternateActive) return;

        _alternateActive = true;
        _alternate.ScrollTop = 0;
        _alternate.ScrollBottom = Rows - 1;
        _alternate.WrapPending = false;
        _alternate.Style = Style.Default;
        if (clear) _alternate.Clear();
        _alternate.Row = 0;
        _alternate.Column = 0;
        MarkDirty();
    }

    public void ExitAlternateScreen(bool clear)
    {
        if (!_alternateActive) return;
        _alternateActive = false;
        if (clear) _alternate.Clear();
        MarkDirty();
    }

    // ------------------------------------------------------------------
    // 字符宽度
    // ------------------------------------------------------------------

    /// <summary>简化的 East Asian Width：0=组合字符，2=宽字符，1=其余。</summary>
    public static int RuneWidth(Rune rune)
    {
        int v = rune.Value;
        if (v == 0) return 0;
        if (v < 0x20 || (v >= 0x7F && v < 0xA0)) return 0;

        if ((v >= 0x0300 && v <= 0x036F) ||
            (v >= 0x1AB0 && v <= 0x1AFF) ||
            (v >= 0x1DC0 && v <= 0x1DFF) ||
            (v >= 0x20D0 && v <= 0x20FF) ||
            (v >= 0xFE20 && v <= 0xFE2F))
            return 0;

        if (v >= 0x1100 &&
            (v <= 0x115F ||
             v == 0x2329 || v == 0x232A ||
             (v >= 0x2E80 && v <= 0xA4CF && v != 0x303F) ||
             (v >= 0xAC00 && v <= 0xD7A3) ||
             (v >= 0xF900 && v <= 0xFAFF) ||
             (v >= 0xFE10 && v <= 0xFE19) ||
             (v >= 0xFE30 && v <= 0xFE6F) ||
             (v >= 0xFF00 && v <= 0xFF60) ||
             (v >= 0xFFE0 && v <= 0xFFE6) ||
             (v >= 0x1F300 && v <= 0x1FAFF) ||
             (v >= 0x20000 && v <= 0x3FFFD)))
            return 2;

        return 1;
    }
}
