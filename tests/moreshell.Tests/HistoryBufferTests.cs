using moreshell.Input;

namespace moreshell.Tests;

/// <summary>L2 行编辑的 ↑/↓ 历史缓冲。</summary>
public class HistoryBufferTests
{
    [Fact]
    public void Add_ignores_blank_lines_and_consecutive_duplicates()
    {
        var history = new HistoryBuffer();
        history.Add("dir");
        history.Add("   ");
        history.Add("dir");
        history.Add("cd /tmp");

        Assert.Equal(new[] { "dir", "cd /tmp" }, history.Items);
    }

    [Fact]
    public void Previous_and_Next_walk_history_and_restore_the_draft()
    {
        var history = new HistoryBuffer();
        history.Add("one");
        history.Add("two");
        history.BeginEdit();

        Assert.Equal("two", history.Previous("draft"));
        Assert.Equal("one", history.Previous("draft"));
        Assert.Null(history.Previous("draft"));

        Assert.Equal("two", history.Next());
        Assert.Equal("draft", history.Next());
        Assert.Null(history.Next());
    }

    [Fact]
    public void Add_drops_the_oldest_entry_when_capacity_is_exceeded()
    {
        var history = new HistoryBuffer(capacity: 2);
        history.Add("1");
        history.Add("2");
        history.Add("3");

        Assert.Equal(new[] { "2", "3" }, history.Items);
    }

    [Fact]
    public void BeginEdit_resets_navigation_after_committing_a_recalled_entry()
    {
        var history = new HistoryBuffer();
        history.Add("old");
        history.BeginEdit();
        Assert.Equal("old", history.Previous(""));

        history.Add("old-again");

        Assert.Equal(2, history.Count);
        Assert.Equal("old-again", history.Previous(""));   // Add 后游标回到末尾
    }
}
