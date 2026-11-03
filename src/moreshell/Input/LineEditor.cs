using System.Globalization;
using System.Text;
using moreshell.Shells;

namespace moreshell.Input;

/// <summary>
/// L2 行式 REPL 的行编辑器：↑/↓ 历史、Tab 补全和基础行内编辑。
///
/// 只在真实终端下使用；stdin/stdout 被重定向时调用方应退回 <see cref="Console.ReadLine"/>。
/// Ctrl+C 只取消当前行，不退出程序；Ctrl+D / Ctrl+Z 在空行上表示 EOF。
/// </summary>
public sealed class LineEditor
{
    private readonly HistoryBuffer _history = new();

    public HistoryBuffer History => _history;

    /// <summary>返回 null 表示输入流结束（EOF）。</summary>
    public string? ReadLine(string prompt, CompletionContext completion)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(completion);

        _history.BeginEdit();
        var buffer = new StringBuilder();
        int cursor = 0;

        bool restoreCtrlC = false;
        bool originalCtrlC = false;
        try
        {
            try
            {
                originalCtrlC = Console.TreatControlCAsInput;
                Console.TreatControlCAsInput = true;
                restoreCtrlC = !originalCtrlC;
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                // 取不到控制台模式时仍可编辑，只是 Ctrl+C 会按默认信号处理
            }

            Repaint(prompt, buffer, cursor);

            while (true)
            {
                ConsoleKeyInfo key;
                try { key = Console.ReadKey(intercept: true); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException) { return Console.ReadLine(); }

                bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
                bool alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        return Finish(buffer.ToString());

                    case ConsoleKey.UpArrow:
                        if (!alt) Recall(_history.Previous(buffer.ToString()), prompt, buffer, ref cursor);
                        break;

                    case ConsoleKey.DownArrow:
                        if (!alt) Recall(_history.Next(), prompt, buffer, ref cursor);
                        break;

                    case ConsoleKey.LeftArrow:
                        if (cursor > 0) { cursor--; Repaint(prompt, buffer, cursor); }
                        break;

                    case ConsoleKey.RightArrow:
                        if (cursor < buffer.Length) { cursor++; Repaint(prompt, buffer, cursor); }
                        break;

                    case ConsoleKey.Home:
                        cursor = 0;
                        Repaint(prompt, buffer, cursor);
                        break;

                    case ConsoleKey.End:
                        cursor = buffer.Length;
                        Repaint(prompt, buffer, cursor);
                        break;

                    case ConsoleKey.Backspace:
                        if (cursor > 0)
                        {
                            buffer.Remove(cursor - 1, 1);
                            cursor--;
                            Repaint(prompt, buffer, cursor);
                        }
                        break;

                    case ConsoleKey.Delete:
                        if (cursor < buffer.Length)
                        {
                            buffer.Remove(cursor, 1);
                            Repaint(prompt, buffer, cursor);
                        }
                        break;

                    case ConsoleKey.Tab:
                        Complete(completion, prompt, buffer, ref cursor);
                        break;

                    case ConsoleKey.Escape:
                        buffer.Clear();
                        cursor = 0;
                        Repaint(prompt, buffer, cursor);
                        break;

                    default:
                        // 有些环境不把 \t 映射成 ConsoleKey.Tab，用 KeyChar 兜底
                        if (key.KeyChar == '\t')
                        {
                            Complete(completion, prompt, buffer, ref cursor);
                            break;
                        }
                        if (ctrl)
                        {
                            // Ctrl+D / Ctrl+Z 在空行上表示 EOF
                            if (HandleControlKey(key.Key, prompt, buffer, ref cursor))
                            {
                                Console.WriteLine();
                                return null;
                            }
                        }
                        else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                        {
                            buffer.Insert(cursor, key.KeyChar);
                            cursor++;
                            Repaint(prompt, buffer, cursor);
                        }
                        break;
                }
            }
        }
        finally
        {
            if (restoreCtrlC)
            {
                try { Console.TreatControlCAsInput = originalCtrlC; }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
            }
        }
    }

    private string Finish(string line)
    {
        _history.Add(line);
        return line;
    }

    private static void Recall(string? line, string prompt, StringBuilder buffer, ref int cursor)
    {
        if (line is null) return;
        buffer.Clear();
        buffer.Append(line);
        cursor = buffer.Length;
        Repaint(prompt, buffer, cursor);
    }

    /// <summary>返回 true 表示请求 EOF（Ctrl+D / Ctrl+Z 且当前行为空）。</summary>
    private static bool HandleControlKey(ConsoleKey key, string prompt, StringBuilder buffer, ref int cursor)
    {
        switch (key)
        {
            case ConsoleKey.C:
                Console.WriteLine("^C");
                buffer.Clear();
                cursor = 0;
                // 必须重画：否则提示符一直不出现，只剩屏幕上残留的旧输入，
                // 用户会以为程序卡死（实际下一个普通字符按下才会恢复）
                Repaint(prompt, buffer, cursor);
                break;

            case ConsoleKey.L:
                Console.Write("\x1b[2J\x1b[H");
                Repaint(prompt, buffer, cursor);
                break;

            case ConsoleKey.A:
                cursor = 0;
                Repaint(prompt, buffer, cursor);
                break;

            case ConsoleKey.E:
                cursor = buffer.Length;
                Repaint(prompt, buffer, cursor);
                break;

            case ConsoleKey.U:
                buffer.Remove(0, cursor);
                cursor = 0;
                Repaint(prompt, buffer, cursor);
                break;

            case ConsoleKey.K:
                buffer.Remove(cursor, buffer.Length - cursor);
                Repaint(prompt, buffer, cursor);
                break;

            case ConsoleKey.W:
                DeletePreviousWord(buffer, ref cursor);
                Repaint(prompt, buffer, cursor);
                break;

            case ConsoleKey.D:
                if (buffer.Length == 0) return true;
                if (cursor < buffer.Length)
                {
                    buffer.Remove(cursor, 1);
                    Repaint(prompt, buffer, cursor);
                }
                break;

            case ConsoleKey.Z:
                if (buffer.Length == 0) return true;
                break;
        }

        return false;
    }

    private static void DeletePreviousWord(StringBuilder buffer, ref int cursor)
    {
        int start = cursor;
        while (start > 0 && char.IsWhiteSpace(buffer[start - 1])) start--;
        while (start > 0 && !char.IsWhiteSpace(buffer[start - 1])) start--;
        buffer.Remove(start, cursor - start);
        cursor = start;
    }

    private static void Complete(CompletionContext context, string prompt, StringBuilder buffer, ref int cursor)
    {
        var set = Completion.Complete(buffer.ToString(), cursor, context);
        if (set is null)
        {
            // 明确提示“按到了但没候选”，避免看起来像 Tab 没反应
            Console.WriteLine();
            Console.WriteLine("（无匹配）");
            Repaint(prompt, buffer, cursor);
            return;
        }

        string typed = buffer.ToString(set.TokenStart, set.TokenLength);

        if (set.Candidates.Count == 1)
        {
            string candidate = set.Candidates[0];
            string replacement = Completion.IsDirectoryCandidate(candidate) ? candidate : candidate + " ";
            Replace(buffer, set.TokenStart, set.TokenLength, replacement, ref cursor);
            Repaint(prompt, buffer, cursor);
            return;
        }

        string common = Completion.CommonPrefix(set.Candidates);
        if (common.Length > typed.Length)
        {
            Replace(buffer, set.TokenStart, set.TokenLength, common, ref cursor);
            Repaint(prompt, buffer, cursor);
            return;
        }

        ShowCandidates(prompt, buffer, cursor, set.Candidates);
    }

    private static void Replace(StringBuilder buffer, int start, int length, string text, ref int cursor)
    {
        buffer.Remove(start, length);
        buffer.Insert(start, text);
        cursor = start + text.Length;
    }

    private static void ShowCandidates(
        string prompt, StringBuilder buffer, int cursor, IReadOnlyList<string> candidates)
    {
        Console.WriteLine();

        const int maximumShown = 100;
        int width = SafeWindowWidth();
        var line = new StringBuilder();
        int shown = 0;

        foreach (var candidate in candidates)
        {
            if (shown >= maximumShown) break;

            if (line.Length > 0 && line.Length + candidate.Length + 2 > width)
            {
                Console.WriteLine(line.ToString());
                line.Clear();
            }

            if (line.Length > 0) line.Append("  ");
            line.Append(candidate);
            shown++;
        }

        if (line.Length > 0) Console.WriteLine(line.ToString());
        if (candidates.Count > shown)
            Console.WriteLine($"... 还有 {candidates.Count - shown} 项，请继续输入以缩小范围");

        Repaint(prompt, buffer, cursor);
    }

    private static int SafeWindowWidth()
    {
        try { return Math.Max(20, Console.WindowWidth); }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { return 80; }
    }

    /// <summary>重画当前行，并把光标移到 <paramref name="cursor"/> 对应的位置。</summary>
    private static void Repaint(string prompt, StringBuilder buffer, int cursor)
    {
        Console.Write('\r');
        Console.Write(prompt);
        Console.Write(buffer.ToString());
        Console.Write("\x1b[K");

        int back = DisplayWidth(buffer.ToString(), cursor, buffer.Length - cursor);
        if (back > 0)
            Console.Write("\x1b[" + back.ToString(CultureInfo.InvariantCulture) + "D");
    }

    private static int DisplayWidth(string text, int start, int count)
    {
        if (count <= 0) return 0;

        int width = 0;
        foreach (var rune in text.AsSpan(start, count).EnumerateRunes())
        {
            int value = rune.Value;
            if (value < 0x20 || (value >= 0x7F && value < 0xA0)) continue;
            width += IsWide(value) ? 2 : 1;
        }
        return width;
    }

    private static bool IsWide(int value)
        => value is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD;
}
