using moreshell.Shells;

namespace moreshell.Input;

/// <summary>补全所需的上下文：当前 shell、虚拟工作目录和全部可用 profile。</summary>
public sealed record CompletionContext(
    ShellProfile Shell,
    string WorkingDirectory,
    IReadOnlyList<ShellProfile> Profiles,
    bool IncludePathCommands = true);

/// <summary>一次补全的候选集合。TokenStart/TokenLength 指向输入行中要替换的片段。</summary>
public sealed record CompletionSet(int TokenStart, int TokenLength, IReadOnlyList<string> Candidates);

/// <summary>
/// L2 行式模式的“尽力而为”补全。
///
/// 只能看到 App 自己的内建命令、PATH 里的可执行文件以及当前虚拟目录的文件系统；
/// 看不到 shell 的别名、函数、变量和 cmdlet，因此不替代 L3 里由原 shell 提供的补全。
/// 目录候选以路径分隔符结尾，文件/命令候选由编辑器追加空格。
/// </summary>
public static class Completion
{
    private static readonly Lazy<IReadOnlyList<string>> PathCommands =
        new(LoadPathCommands, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly string[] CmdBuiltins =
    {
        "cd", "cls", "dir", "echo", "set", "type", "copy", "del", "move", "ren",
        "md", "rd", "where", "start", "tasklist", "taskkill", "chcp", "exit", "help", "?",
    };

    private static readonly string[] PowerShellBuiltins =
    {
        "cd", "cls", "clear", "pwd", "exit", "help", "?",
        "Get-ChildItem", "Set-Location", "Get-Location", "Get-Process", "Get-Help",
        "Write-Host", "Test-Path", "New-Item", "Remove-Item", "Select-String",
    };

    private static readonly string[] BashBuiltins =
    {
        "cd", "clear", "pwd", "exit", "help", "?",
        "ls", "cat", "grep", "find", "sed", "awk", "chmod", "chown",
        "export", "source", "which", "echo", "git", "make",
    };

    /// <summary>提前在后台构建 PATH 命令表，避免第一次按 Tab 时同步扫描所有 PATH 目录。</summary>
    public static void WarmUp() => _ = PathCommands.Value;

    /// <summary>返回 null 表示当前 token 没有候选。</summary>
    public static CompletionSet? Complete(string text, int cursor, CompletionContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);

        cursor = Math.Clamp(cursor, 0, text.Length);
        int tokenStart = FindTokenStart(text, cursor);
        string rawToken = text[tokenStart..cursor];

        // 带引号的 token：去掉引号参与匹配，候选再按同样方式包回去
        char? quote = rawToken.Length > 0 && (rawToken[0] == '"' || rawToken[0] == '\'')
            ? rawToken[0]
            : null;
        string token = quote is null ? rawToken : rawToken[1..];
        if (quote is not null && token.EndsWith(quote.Value))
            token = token[..^1];

        bool firstToken = IsFirstToken(text, tokenStart);
        bool hasSeparator = token.IndexOfAny(new[] { '/', '\\' }) >= 0;
        bool dotPath = token.StartsWith('.');
        bool homePath = token.StartsWith("~/") || token.StartsWith("~\\");
        bool shellSwitch = firstToken && quote is null && token.StartsWith('~') && !hasSeparator;

        // ~ps Get-Ch / ~cmd di：第二个 token 是目标 shell 的命令
        ShellProfile? targetShell = quote is null && !firstToken
            ? ResolveTargetShell(text, tokenStart, context)
            : null;

        var candidates = new List<string>();

        if (shellSwitch)
        {
            AddShellSwitches(candidates, token, context.Profiles);
        }
        else if (quote is null && firstToken && !hasSeparator && !dotPath && !homePath)
        {
            AddBuiltins(candidates, token, context.Shell);

            // WSL 的命令运行在发行版里，宿主机 PATH 对它没有意义
            if (context.IncludePathCommands && context.Shell.Kind != ShellKind.Wsl)
                AddMatches(candidates, token, PathCommands.Value, CommandComparison(context.Shell.Kind));
        }
        else if (quote is null && targetShell is not null && !hasSeparator && !dotPath && !homePath)
        {
            AddBuiltins(candidates, token, targetShell);
            if (context.IncludePathCommands && targetShell.Kind != ShellKind.Wsl)
                AddMatches(candidates, token, PathCommands.Value, CommandComparison(targetShell.Kind));
        }
        else
        {
            AddPathMatches(candidates, token, context);
        }

        if (candidates.Count == 0) return null;

        if (quote is char quoteChar)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                candidates[i] = IsDirectoryCandidate(candidates[i])
                    ? quoteChar + candidates[i]                 // 目录还开着引号，继续补
                    : quoteChar + candidates[i] + quoteChar;    // 文件/命令补完并闭合引号
            }
        }

        SortAndDedupe(candidates, context.Shell.UsesWindowsPath);
        return new CompletionSet(tokenStart, rawToken.Length, candidates);
    }

    /// <summary>判断当前 token 是否位于 <c>~shell</c> 之后的第一个参数位置。</summary>
    private static ShellProfile? ResolveTargetShell(string text, int tokenStart, CompletionContext context)
    {
        string prefix = text[..tokenStart].Trim();
        if (prefix.Length < 2 || prefix[0] != '~') return null;
        if (prefix.IndexOfAny(new[] { ' ', '\t' }) >= 0) return null;

        return ShellRegistry.Find(context.Profiles, prefix[1..]);
    }

    public static bool IsDirectoryCandidate(string candidate)
        => candidate.EndsWith('/') || candidate.EndsWith('\\');

    public static string CommonPrefix(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return "";

        string prefix = items[0];
        for (int n = 1; n < items.Count && prefix.Length > 0; n++)
        {
            string item = items[n];
            int max = Math.Min(prefix.Length, item.Length);
            int i = 0;
            while (i < max && char.ToUpperInvariant(prefix[i]) == char.ToUpperInvariant(item[i]))
                i++;
            prefix = prefix[..i];
        }
        return prefix;
    }

    // ------------------------------------------------------------------

    private static void AddShellSwitches(List<string> result, string token, IReadOnlyList<ShellProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            AddIfMatches(result, token, "~" + profile.Id, StringComparison.OrdinalIgnoreCase);
            foreach (var alias in profile.Aliases)
                AddIfMatches(result, token, "~" + alias, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AddBuiltins(List<string> result, string token, ShellProfile shell)
    {
        string[] builtins = shell.Kind switch
        {
            ShellKind.Cmd => CmdBuiltins,
            ShellKind.PowerShell => PowerShellBuiltins,
            _ => BashBuiltins,
        };

        var comparison = CommandComparison(shell.Kind);
        foreach (var builtin in builtins)
            AddIfMatches(result, token, builtin, comparison);
    }

    private static void AddMatches(
        List<string> result, string token, IReadOnlyList<string> options, StringComparison comparison)
    {
        foreach (var option in options)
            AddIfMatches(result, token, option, comparison);
    }

    private static void AddIfMatches(List<string> result, string token, string candidate, StringComparison comparison)
    {
        if (candidate.StartsWith(token, comparison))
            result.Add(candidate);
    }

    private static void AddPathMatches(List<string> result, string token, CompletionContext context)
    {
        int separatorIndex = token.LastIndexOfAny(new[] { '/', '\\' });
        string directoryPart = separatorIndex >= 0 ? token[..(separatorIndex + 1)] : "";
        string prefix = separatorIndex >= 0 ? token[(separatorIndex + 1)..] : token;

        char separator = separatorIndex >= 0
            ? token[separatorIndex]
            : (context.Shell.UsesWindowsPath ? '\\' : '/');

        string? baseDirectory = ResolveBaseDirectory(directoryPart, context);
        if (baseDirectory is null) return;

        var comparison = context.Shell.UsesWindowsPath || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        bool includeHidden = prefix.StartsWith('.');
        var directory = new DirectoryInfo(baseDirectory);
        try
        {
            if (!directory.Exists) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        try
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                string name = entry.Name;
                if (name.StartsWith('.') && !includeHidden) continue;
                if (!name.StartsWith(prefix, comparison)) continue;

                string candidate = directoryPart + name;
                if (entry is DirectoryInfo) candidate += separator;
                result.Add(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 目录在枚举中途不可读时保留已经拿到的候选
        }
    }

    /// <summary>
    /// 把 token 的目录部分解析成宿主机可访问的路径。
    /// WSL 的 Linux 路径在 Windows 上不可访问，目录不存在时自然返回空候选。
    /// </summary>
    private static string? ResolveBaseDirectory(string directoryPart, CompletionContext context)
    {
        if (directoryPart.Length == 0)
            return ExpandHome(context.WorkingDirectory, context);

        if (IsHomePath(directoryPart))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length == 0) return null;

            string rest = directoryPart[2..];
            return rest.Length == 0 ? home : Path.Combine(home, rest);
        }

        if (Path.IsPathRooted(directoryPart)) return directoryPart;

        string cwd = ExpandHome(context.WorkingDirectory, context);
        return Path.Combine(cwd, directoryPart);
    }

    private static string ExpandHome(string path, CompletionContext context)
    {
        if (path == "~")
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home.Length > 0 ? home : path;
        }

        if (IsHomePath(path))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0) return Path.Combine(home, path[2..]);
        }

        return path;
    }

    private static bool IsHomePath(string path)
        => path.StartsWith("~/") || path.StartsWith("~\\");

    private static StringComparison CommandComparison(ShellKind kind)
        => kind is ShellKind.Cmd or ShellKind.PowerShell
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static void SortAndDedupe(List<string> items, bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        items.Sort(comparer);
        for (int i = items.Count - 1; i > 0; i--)
            if (string.Equals(items[i], items[i - 1], comparison))
                items.RemoveAt(i);
    }

    private static int FindTokenStart(string text, int cursor)
    {
        int i = cursor;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        return i;
    }

    private static bool IsFirstToken(string text, int tokenStart)
    {
        for (int i = 0; i < tokenStart; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }

    private static IReadOnlyList<string> LoadPathCommands()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".cmd", ".bat", ".com", ".ps1",
        };

        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir;
            try { dir = raw.Trim().Trim('"'); }
            catch { continue; }
            if (dir.Length == 0) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    if (name.Length == 0) continue;

                    if (OperatingSystem.IsWindows())
                    {
                        string extension = Path.GetExtension(name);
                        if (extension.Length > 0 && executableExtensions.Contains(extension))
                            name = name[..^extension.Length];
                        if (name.Length > 0) names.Add(name);
                        continue;
                    }

                    try
                    {
                        var mode = File.GetUnixFileMode(file);
                        if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                            names.Add(name);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        // 单个文件不可读不影响其余候选
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // PATH 里的无效目录直接跳过
            }
        }

        var list = names.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
