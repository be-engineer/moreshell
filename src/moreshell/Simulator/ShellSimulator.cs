using System.Text.RegularExpressions;
using moreshell.Shells;

namespace moreshell.Simulator;

/// <summary>
/// 一次拦截的结果。
/// <list type="bullet">
///   <item><see cref="Handled"/>=true：命令已被模拟层吃掉，**不再**交给真 shell</item>
///   <item><see cref="Handled"/>=false：原样交给真 shell（含 Message 时先打印提示）</item>
/// </list>
/// </summary>
public sealed record InterceptOutcome(bool Handled, string? Message = null, bool ClearScreen = false, bool ShouldExit = false)
{
    public static readonly InterceptOutcome Pass = new(false);
    public static InterceptOutcome Print(string message) => new(true, message);
}

/// <summary>
/// 极简 shell 模拟层（Tier A 范围）。
///
/// 只做三件事：拦截 <c>cd</c> / <c>cls</c> / <c>exit</c>、维护**每个 shell 各自独立**的虚拟当前目录、
/// 以及在真正起进程前把它注入进去。
///
/// 为什么需要它：L2 是一次性 <see cref="System.Diagnostics.Process"/>，进程退出后目录就没了。
/// 没有这一层，连续两条命令里前一条的 <c>cd</c> 会静默失效——那是最难排查的一类 bug。
///
/// 已知边界（原型接受的取舍）：
/// <list type="bullet">
///   <item>命令里出现 <c>&amp;&amp; | ; &gt;</c> 等控制符时**不拦截**，交给真 shell，
///        代价是虚拟目录可能与真实目录脱节——宁可可预测，不做半吊子解析。</item>
///   <item>Git Bash 的 <c>/home/...</c> 之类无法映射回 Windows 的路径无法持久化，会明确报错而不是静默漂移。</item>
///   <item>WSL 目录是 Linux 路径，无法在 Windows 侧校验是否存在，因此只做归一化不做过滤。</item>
/// </list>
/// </summary>
public sealed class ShellSimulator
{
    private sealed class ShellState
    {
        public string Cwd = "";
        public string? Previous;
    }

    private readonly Dictionary<string, ShellState> _states = new(StringComparer.OrdinalIgnoreCase);

    public ShellSimulator(IReadOnlyList<ShellProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        foreach (var p in profiles)
            _states[p.Id] = new ShellState { Cwd = p.DefaultWorkingDirectory };
    }

    /// <summary>该 shell 体系下应有的工作目录（注入给子进程用）。</summary>
    public string GetWorkingDirectory(ShellProfile profile)
        => _states.TryGetValue(profile.Id, out var s) && s.Cwd.Length > 0
            ? s.Cwd
            : profile.DefaultWorkingDirectory;

    // ------------------------------------------------------------------

    public InterceptOutcome Intercept(ShellProfile profile, string rawCommand)
    {
        var command = rawCommand.Trim();
        if (command.Length == 0)
            return new InterceptOutcome(true);          // 空输入：静默

        if (!_states.TryGetValue(profile.Id, out var state))
            state = new ShellState { Cwd = profile.DefaultWorkingDirectory };

        bool windows = profile.UsesWindowsPath;
        var (verb, remainder) = SplitVerb(command);
        string v = windows ? verb.ToLowerInvariant() : verb;   // bash 区分大小写

        if (IsExit(v)) return new InterceptOutcome(true, ShouldExit: true);

        if (IsClear(v, profile.Kind)) return new InterceptOutcome(true, ClearScreen: true);

        if (IsPwd(v, profile.Kind))
            return InterceptOutcome.Print(state.Cwd);

        if (IsCd(v, profile.Kind))
            return InterceptCd(profile, state, command, remainder, windows);

        return InterceptOutcome.Pass;
    }

    // ------------------------------------------------------------------
    // cd
    // ------------------------------------------------------------------

    private static InterceptOutcome InterceptCd(
        ShellProfile profile, ShellState state, string command, string remainder, bool windows)
    {
        // 复杂命令一律不拦截：与其解析一半，不如完全交给真 shell
        if (HasShellControl(command))
            return InterceptOutcome.Pass;

        if (remainder.Length == 0)
        {
            // POSIX 语义：裸 cd 回 $HOME；cmd/PowerShell 语义：打印当前目录
            if (!windows)
            {
                state.Previous = state.Cwd;
                state.Cwd = "~";
                return new InterceptOutcome(true);
            }
            return InterceptOutcome.Print(state.Cwd);
        }

        if (remainder.StartsWith('-'))                  // cd -L / cd -P 等开关
            return InterceptOutcome.Print("原型暂不支持该 cd 开关。");

        if (windows)
            return InterceptWindowsCd(profile, state, remainder);

        return InterceptLinuxCd(profile, state, remainder);
    }

    private static InterceptOutcome InterceptWindowsCd(ShellProfile profile, ShellState state, string rest)
    {
        var arg = StripQuotes(rest.Trim());

        // cmd 的 /d 开关（换盘符）
        if (arg.StartsWith("/d ", StringComparison.OrdinalIgnoreCase) || arg == "/d")
            arg = arg.Length > 2 ? arg[2..].TrimStart() : "";

        // PowerShell 的 ~ 表示家目录；cmd 里 ~ 只是普通字符（cd ~ 报"找不到路径"才是保真的）
        if (profile.Kind == ShellKind.PowerShell)
            arg = ExpandTilde(arg);

        arg = StripQuotes(ExpandPercentVars(arg)).Trim();

        if (arg.Length == 0)
            return InterceptOutcome.Print(state.Cwd);

        if (Regex.IsMatch(arg, @"^[A-Za-z]:$"))
            return InterceptOutcome.Pass;             // `cd D:` 语义复杂（切盘不切目录），交给真 shell

        // Git Bash 习惯用 /d/app 这种 MSYS 路径；在 Linux 上直接作为本地路径处理
        if (arg.StartsWith('/'))
        {
            if (!OperatingSystem.IsWindows())
            {
                string linuxCandidate = Path.GetFullPath(arg);
                if (!Directory.Exists(linuxCandidate))
                    return InterceptOutcome.Print("系统找不到指定的路径。");
                state.Cwd = linuxCandidate;
                return new InterceptOutcome(true);
            }
            var converted = MsysToWindows(arg);
            if (converted is null)
                return InterceptOutcome.Print(
                    $"原型限制：无法把 {arg} 映射为 Windows 路径，该路径不能跨命令持久化。请改用 /d/... 或 D:\\... 形式。");
            arg = converted;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.IsPathRooted(arg) ? arg : Path.Combine(state.Cwd, arg));
        }
        catch (Exception ex)
        {
            return InterceptOutcome.Print($"路径无效：{ex.Message}");
        }

        if (!Directory.Exists(candidate))
            return InterceptOutcome.Print("系统找不到指定的路径。");

        state.Previous = state.Cwd;
        state.Cwd = candidate;
        return new InterceptOutcome(true);            // 成功的 cd 无输出
    }

    private static InterceptOutcome InterceptLinuxCd(ShellProfile profile, ShellState state, string rest)
    {
        var arg = StripQuotes(rest.Trim());
        if (arg.Length == 0)
        {
            state.Previous = state.Cwd;
            state.Cwd = "~";
            return new InterceptOutcome(true);
        }

        if (profile.Kind == ShellKind.Wsl)
        {
            // WSL 里允许写 Windows 风格路径（cd D:\code / D:/code），转成发行版里的 /mnt/<盘>/…
            var converted = WindowsToWslPath(arg);
            if (converted is not null)
                arg = converted;
        }

        // 无法在 Windows 侧校验 WSL 目录是否存在（那是发行版里的文件系统），只做归一化；
        // 真正的本地 POSIX shell（bash/sh）则如实校验——不存在就报错，而不是静默吞掉 cd
        var baseDir = arg.StartsWith('/')
            ? arg
            : state.Cwd == "~" ? "~/" + arg
            : state.Cwd.TrimEnd('/') + "/" + arg;

        state.Previous = state.Cwd;
        var normalized = NormalizeLinux(baseDir);

        // 校验只针对本地 POSIX shell（bash/sh 跑在宿主文件系统上）；
        // WSL 的目录在发行版文件系统里，宿主侧无法校验，绝不能拦
        if (profile.Kind != ShellKind.Wsl && !OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(ExpandHomeForCheck(normalized)))
                return InterceptOutcome.Print("没有那个文件或目录。");
        }

        state.Cwd = normalized;
        return new InterceptOutcome(true);
    }

    /// <summary>把归一化后的 Linux 路径映射成宿主机用来校验的真实路径。</summary>
    private static string ExpandHomeForCheck(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    /// <summary>
    /// Windows 路径 → WSL 视角的 <c>/mnt/&lt;盘&gt;/…</c> 路径。
    /// 这也是 wsl.exe 自身的默认行为：从某个 Windows 目录启动时，里面的当前目录就是对应的
    /// <c>/mnt/&lt;盘&gt;/…</c>。UNC、相对路径等无法映射时返回 null。
    /// </summary>
    public static string? WindowsToWslPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)) return null;   // UNC 不进 WSL

        // 匹配 X:\、X:/ 或裸盘符 X:
        var m = Regex.Match(trimmed, @"^([A-Za-z]):(?:[\\/](.*))?$");
        if (!m.Success) return null;

        string drive = m.Groups[1].Value.ToLowerInvariant();
        string tail = m.Groups.Count > 2 ? m.Groups[2].Value.Replace('\\', '/') : "";
        tail = tail.TrimEnd('/');

        return tail.Length == 0 ? "/mnt/" + drive : "/mnt/" + drive + "/" + tail;
    }

    /// <summary>归一化 Linux 路径：<c>~</c> 与 <c>/</c> 两套根、去掉 <c>.</c> / <c>..</c>。</summary>
    public static string NormalizeLinux(string path)
    {
        if (path == "~") return "~";

        bool home = path.StartsWith("~/", StringComparison.Ordinal);
        bool absolute = path.StartsWith('/');

        var tail = home ? path[2..] : absolute ? path[1..] : path;
        var stack = new List<string>();

        foreach (var seg in tail.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }

        if (home) return stack.Count == 0 ? "~" : "~/" + string.Join("/", stack);
        if (absolute) return "/" + string.Join("/", stack);
        return string.Join("/", stack);
    }

    /// <summary><c>/d/app</c> → <c>D:\app</c>；无法映射（如 <c>/home/x</c>）返回 null。</summary>
    public static string? MsysToWindows(string path)
    {
        var m = Regex.Match(path, @"^/([A-Za-z])(/.*)?$");
        if (!m.Success) return null;

        char drive = char.ToUpperInvariant(m.Groups[1].Value[0]);
        var rest = m.Groups[2].Value.Replace('/', '\\').TrimStart('\\');
        return rest.Length == 0 ? drive + ":\\" : drive + ":\\" + rest;
    }

    // ------------------------------------------------------------------
    // 管道分页器（| more / | less）
    // ------------------------------------------------------------------

    /// <summary>
    /// 从命令末尾抽出独立的 <c>| more</c> / <c>| less</c> 分页器。
    /// 一次性子进程没有 TTY，真 more/less 无法读键翻页，
    /// 交出去只会一次性倾泻；App 接管后由内置分页器逐屏显示。
    /// </summary>
    /// <returns>false 表示不拦截（没有管道、管道末端不是 more/less、或管道仍在引号内）。</returns>
    public static bool TryExtractPipePager(string command, out string inner)
    {
        inner = command;
        int last = command.LastIndexOf('|');
        if (last < 0) return false;

        // 简单引号配平检查：引号数量为奇数说明最后一个管道在引号里，属于打印内容
        string before = command[..last];
        if (CountOdd(before, '"') || CountOdd(before, '\'')) return false;

        var tail = command[(last + 1)..].Trim();
        if (!Regex.IsMatch(tail, @"^(more|less)\b", RegexOptions.IgnoreCase)) return false;

        inner = before.TrimEnd();
        return inner.Length > 0;
    }

    private static bool CountOdd(string s, char quote) => s.Count(c => c == quote) % 2 == 1;

    // ------------------------------------------------------------------
    // 词法
    // ------------------------------------------------------------------

    private static (string Verb, string Remainder) SplitVerb(string command)
    {
        int i = 0;
        while (i < command.Length && !char.IsWhiteSpace(command[i])) i++;
        return (command[..i], i >= command.Length ? "" : command[i..].TrimStart());
    }

    private static bool IsCd(string v, ShellKind kind) => kind switch
    {
        ShellKind.Cmd => v is "cd" or "chdir",
        ShellKind.PowerShell => v is "cd" or "chdir" or "sl" or "set-location",
        _ => v == "cd",
    };

    private static bool IsClear(string v, ShellKind kind) => kind switch
    {
        ShellKind.Cmd => v == "cls",
        ShellKind.PowerShell => v is "cls" or "clear" or "clear-host",
        _ => v == "clear",
    };

    private static bool IsPwd(string v, ShellKind kind) => kind switch
    {
        ShellKind.Cmd => false,                       // cmd 没有 pwd
        ShellKind.PowerShell => v is "pwd" or "get-location",
        _ => v == "pwd",
    };

    private static bool IsExit(string v) => v is "exit" or "quit";

    /// <summary>出现 shell 控制符时不拦截，避免"解析一半"造成状态错乱。</summary>
    private static bool HasShellControl(string command)
        => command.Contains("&&", StringComparison.Ordinal)
           || command.Contains("||", StringComparison.Ordinal)
           || command.Contains(';')
           || command.Contains('|')
           || command.Contains('>')
           || command.Contains('<');

    private static string StripQuotes(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s.StartsWith('"') && s.EndsWith('"') ? s[1..^1] : s;
    }

    private static string ExpandPercentVars(string s)
        => Regex.Replace(s, "%([^%]+)%", m =>
        {
            var value = Environment.GetEnvironmentVariable(m.Groups[1].Value);
            return string.IsNullOrEmpty(value) ? m.Value : value;
        });

    /// <summary>
    /// 把 <c>~</c> / <c>~/x</c> 展开成家目录。只对 PowerShell 做——
    /// cmd 里 <c>~</c> 没有特殊含义，<c>cd ~</c> 报"找不到路径"才是正确行为。
    /// </summary>
    private static string ExpandTilde(string arg)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (arg == "~") return home;
        if (arg.StartsWith("~/", StringComparison.Ordinal) ||
            arg.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(home, arg[2..]);
        return arg;
    }
}
