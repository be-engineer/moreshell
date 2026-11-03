using System.Text;
using moreshell.Input;
using moreshell.Pty;
using moreshell.Render;
using moreshell.Runner;
using moreshell.Session;
using moreshell.Shells;
using moreshell.Simulator;
using moreshell.Vt;

namespace moreshell;

/// <summary>
/// 多 shell 原型入口（Tier A）。
///
/// 三种模式：
/// <list type="bullet">
///   <item>无参数 —— 交互 REPL，用 <c>~cmd</c> / <c>~ps</c> / <c>~bash</c> / <c>~wsl</c> 切 shell</item>
///   <item><c>--run &lt;shell&gt; &lt;命令&gt;</c> —— 一次性执行并把颜色实时画到控制台，退出码透传</item>
///   <item><c>--json &lt;shell&gt; &lt;命令&gt;</c> —— 一次性执行并输出解析后的结构化结果，供脚本断言</item>
/// </list>
/// </summary>
public static class Program
{
    private static readonly object ConsoleLock = new();

    public static async Task<int> Main(string[] args)
    {
        // 必须设成 UTF-8：默认 CP936 会让我们自己输出的中文在管道里变成 GBK，
        // 那样连验收脚本都没法判断"到底乱没乱"。
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Encodings.EnsureRegistered();

        var detection = ShellRegistry.Create();
        var profiles = detection.Profiles;

        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "-h":
                case "--help":
                case "/?":
                    PrintHelp();
                    return 0;

                case "--list":
                    ListShells(profiles, detection.Warnings);
                    return 0;

                case "--run":
                    return await OneShotAsync(profiles, args[1..], json: false);

                case "--json":
                    return await OneShotAsync(profiles, args[1..], json: true);

                case "--l3":
                    return await InteractiveAsync(profiles, args[1..]);

                default:
                    // 整行当输入，省去再写一次 --run
                    return await OneShotAsync(profiles, args, json: false);
            }
        }

        return await ReplAsync(profiles, detection.Warnings);
    }

    // ==================================================================
    // 一次性执行
    // ==================================================================

    private static async Task<int> OneShotAsync(IReadOnlyList<ShellProfile> profiles, string[] tokens, bool json)
    {
        if (tokens.Length == 0)
        {
            Console.Error.WriteLine("用法：--run|--json <shell> <命令>");
            return 2;
        }

        var (profile, command) = ResolveInvocation(profiles, tokens);
        if (profile is null)
        {
            Console.Error.WriteLine($"未知 shell：{tokens[0]}（用 --list 查看可用项）");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine("命令为空。");
            return 2;
        }

        var report = await ExecuteAsync(profile, command, profile.DefaultWorkingDirectory, live: !json);

        if (!json) return report.Result.ExitCode;

        var result = report.Result;
        Console.WriteLine(Dumper.Dump(new
        {
            shell = profile.Id,
            command,
            exitCode = result.ExitCode,
            durationMs = (long)result.Duration.TotalMilliseconds,
            timedOut = result.TimedOut,
            cancelled = result.Cancelled,
            failure = result.Failure,
            stdoutEncoding = result.StandardOutputEncoding,
            stderrEncoding = result.StandardErrorEncoding,
            stdout = Dumper.DescribeDocument(report.Stdout.Document),
            stderr = Dumper.DescribeDocument(report.Stderr.Document),
        }));

        return result.ExitCode;
    }

    // ==================================================================
    // 持久交互会话（L3）
    // ==================================================================

    private static async Task<int> InteractiveAsync(IReadOnlyList<ShellProfile> profiles, string[] tokens)
    {
        if (profiles.Count == 0)
        {
            Console.Error.WriteLine("没有可用的 shell。");
            return 1;
        }

        if (!PtySession.IsSupported)
        {
            Console.Error.WriteLine("当前平台不支持持久 PTY 会话（需要 Windows 10 1809+ 或带 util-linux script 的 Linux）。");
            return 1;
        }

        ShellProfile? profile;
        if (tokens.Length == 0 || string.IsNullOrWhiteSpace(tokens[0]))
        {
            profile = profiles[0];
        }
        else
        {
            var token = tokens[0].TrimStart('~');
            profile = ShellRegistry.Find(profiles, token);
            if (profile is null)
            {
                Console.Error.WriteLine($"未知 shell：{tokens[0]}（用 --list 查看可用项）");
                return 2;
            }
        }

        try
        {
            return await new InteractiveSession(profile).RunAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"无法启动交互会话：{ex.Message}");
            return 1;
        }
    }

    // ==================================================================
    // 交互 REPL
    // ==================================================================

    private static async Task<int> ReplAsync(IReadOnlyList<ShellProfile> profiles, IReadOnlyList<string> warnings)
    {
        if (profiles.Count == 0)
        {
            Console.Error.WriteLine("没有可用的 shell。");
            return 1;
        }

        var simulator = new ShellSimulator(profiles);
        var current = profiles[0];

        PrintBanner(profiles, warnings);

        // 行编辑用 ANSI 做局部重绘；Windows 下需要显式打开 VT 处理
        using var ansi = ConsoleMode.EnableAnsiOutput();

        // 老 conhost 里鼠标选中复制依赖 QuickEdit；若先前被关掉，这里补开
        using var quickEdit = ConsoleMode.EnsureQuickEditMode();

        // PATH 命令表在后台预热，第一次按 Tab 不用现场扫全部 PATH 目录
        _ = Task.Run(Completion.WarmUp);

        // 输入/输出被重定向时（脚本管道）退回朴素的 Console.ReadLine
        // 只以 stdin 判断：某些 IDE 终端会把 stdout 标记为重定向，但按键仍然是实时按键
        bool interactive = !Console.IsInputRedirected;
        if (!interactive)
        {
            Console.WriteLine("提示：stdin 不是控制台，Tab 补全与 ↑/↓ 历史不可用；");
            Console.WriteLine("      请在真实终端中运行，或用 --l3 使用原 shell 的补全。");
        }
        else
        {
            Console.WriteLine("行编辑已启用：↑/↓ 历史、Tab 补全。复制：终端里选中后按 Enter / 右键，或 Ctrl+Shift+C。");
        }

        var editor = new LineEditor();

        while (true)
        {
            string workingDirectory = simulator.GetWorkingDirectory(current);
            string prompt = ShellColors.Prompt(current, workingDirectory, AllowColor);

            string? line;
            if (interactive)
            {
                line = editor.ReadLine(prompt, new CompletionContext(current, workingDirectory, profiles));
            }
            else
            {
                Console.Write(prompt);
                try { line = Console.ReadLine(); }
                catch (Exception) { line = null; }
            }

            if (line is null) return 0;                    // Ctrl+D / Ctrl+Z / 输入流关闭

            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed is "?") { ListShells(profiles, warnings); continue; }
            if (trimmed is "help" or "--help") { PrintHelp(); continue; }

            var command = trimmed;

            if (trimmed.StartsWith('~'))
            {
                var (token, remainder) = SplitAtFirstSpace(trimmed[1..]);
                if (token.Length == 0) { ListShells(profiles, warnings); continue; }

                var target = ShellRegistry.Find(profiles, token);
                if (target is null)
                {
                    Console.WriteLine($"未知 shell：~{token}（输入 ? 查看可用项）");
                    continue;
                }

                // 只在不带命令时切换当前 shell
                if (remainder.Length == 0)
                {
                    current = target;
                    Console.WriteLine($"已切换到 {current.DisplayName}");
                    continue;
                }

                // 带命令时用目标 shell 执行一次，不改变当前 shell
                var targetOutcome = simulator.Intercept(target, remainder);
                if (targetOutcome.ShouldExit) return 0;
                if (targetOutcome.ClearScreen) { SafeClear(); continue; }
                if (targetOutcome.Message is not null) Console.WriteLine(targetOutcome.Message);
                if (targetOutcome.Handled) continue;

                var (targetExec, targetPager) = SplitPager(remainder);
                var targetResult = await ExecuteAsync(
                    target, targetExec, simulator.GetWorkingDirectory(target), live: true, pager: targetPager);
                ReportStatus(targetResult.Result);
                continue;
            }

            var simOutcome = simulator.Intercept(current, command);
            if (simOutcome.ShouldExit) return 0;
            if (simOutcome.ClearScreen) { SafeClear(); continue; }
            if (simOutcome.Message is not null) Console.WriteLine(simOutcome.Message);
            if (simOutcome.Handled) continue;

            var (execCommand, usePager) = SplitPager(command);
            var cmdResult = await ExecuteAsync(
                current, execCommand, simulator.GetWorkingDirectory(current), live: true, pager: usePager);
            ReportStatus(cmdResult.Result);
        }
    }

    // ==================================================================
    // 执行装配
    // ==================================================================

    private sealed record RunReport(CommandResult Result, VtPipeline Stdout, VtPipeline Stderr);

    private static async Task<RunReport> ExecuteAsync(
        ShellProfile profile, string command, string? workingDirectory, bool live, bool pager = false)
    {
        var stdout = new VtPipeline();
        var stderr = new VtPipeline();

        if (live)
        {
            var writer = new LiveWriter(ConsoleLock);
            stdout.Sink = writer.Write;
            stderr.Sink = writer.Write;
        }

        CommandResult result;
        using (var cts = new CancellationTokenSource())
        using (ConsoleCancel.Subscribe(cts.Cancel))
        {
            // “| more / | less”：改由 App 内置分页器接管（子进程没有 TTY，真 more 无法翻页）
            Pager? pagerInstance = live && pager ? new Pager(ConsoleLock, cts.Token) : null;

            result = await new CommandRunner().RunAsync(new CommandOptions
            {
                Profile = profile,
                Command = command,
                WorkingDirectory = workingDirectory,
                OnStandardOutput = pagerInstance is null
                    ? stdout.Push
                    : text => pagerInstance.Gate(text, stdout.Push),
                OnStandardError = stderr.Push,
            }, cts.Token);
        }

        return new RunReport(result, stdout, stderr);
    }

    /// <summary>
    /// 交互 REPL 下拦截管道末端的 <c>| more</c> / <c>| less</c>，
    /// 改走内置分页器；输出被重定向（脚本管道）时不拆，保持命令原样透传。
    /// </summary>
    private static (string Command, bool Pager) SplitPager(string command)
    {
        if (Console.IsOutputRedirected) return (command, false);
        return ShellSimulator.TryExtractPipePager(command, out var inner)
            ? (inner, true)
            : (command, false);
    }

    /// <summary>命令结束后打印状态；成功时保持安静，出问题时必须显眼。</summary>
    private static void ReportStatus(CommandResult result)
    {
        if (result.Succeeded) return;

        string text = result.Failure is not null
            ? $"✗ {result.Failure}"
            : result.TimedOut ? "✗ 超时，已终止"
            : result.Cancelled ? "✗ 已取消"
            : $"✗ 退出码 {result.ExitCode}";

        var (fg, _) = ConsoleRenderer.ResolveColors(new Style(StyleFlags.None, 9, Colors.Default));
        lock (ConsoleLock)
        {
            var old = Console.ForegroundColor;
            try { Console.ForegroundColor = fg; Console.WriteLine(text); }
            finally { Console.ForegroundColor = old; }
        }
    }

    private static void SafeClear()
    {
        try { Console.Clear(); }
        catch (IOException) { }
    }

    // ==================================================================
    // 帮助与信息
    // ==================================================================

    /// <summary>输出被重定向时不允许 ANSI 落到目标里（会写进文件/管道）。</summary>
    private static bool AllowColor => !Console.IsOutputRedirected;

    private static void PrintBanner(IReadOnlyList<ShellProfile> profiles, IReadOnlyList<string> warnings)
    {
        string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        bool color = AllowColor;

        var header = new StringBuilder();
        header.Append(color ? ShellColors.Good("moreshell") : "moreshell");
        header.Append(" v").Append(version)
              .Append(" —— 一个窗口里在多种 shell 之间切换的自研终端");
        Console.WriteLine(header);
        Console.WriteLine(color
            ? ShellColors.Faint("      命令由一次性子进程执行；需要 vim/top 等全屏程序时用 --l3 进入持久会话。")
            : "      命令由一次性子进程执行；需要 vim/top 等全屏程序时用 --l3 进入持久会话。");
        Console.WriteLine();
        Console.WriteLine("可用 shell：");
        foreach (var p in profiles)
            Console.WriteLine(ShellLine("  ~" + p.Id, p, color));
        Console.WriteLine();
        Console.WriteLine("输入 ? 列出 shell，help 查看帮助，exit 退出。");
        if (warnings.Count > 0)
        {
            Console.WriteLine();
            foreach (var w in warnings) Console.WriteLine(color ? ShellColors.Faint("  提示：" + w) : "  提示：" + w);
        }
        Console.WriteLine();
    }

    /// <summary>shell 列表行：彩色 token + 灰色显示名/别名；先按可见宽度补齐再上色。</summary>
    private static string ShellLine(string token, ShellProfile p, bool color)
    {
        var alias = p.Aliases.Length == 0 ? "" : "（别名：" + string.Join(" / ", p.Aliases) + "）";
        string padded = token.PadRight(9);   // “  ~bash   ”：token 含前导列并对齐
        string segment = color ? ShellColors.ListToken(padded, p, color) : padded;
        return segment + p.DisplayName + ShellColors.ListMinor(alias, color);
    }

    private static void ListShells(IReadOnlyList<ShellProfile> profiles, IReadOnlyList<string> warnings)
    {
        bool color = AllowColor;
        foreach (var p in profiles)
        {
            var alias = p.Aliases.Length == 0 ? "" : "（别名：" + string.Join(" / ", p.Aliases) + "）";
            Console.WriteLine(ShellLine("  ~" + p.Id, p, color));
            Console.WriteLine(ShellColors.ListMinor($"         {p.Executable}", color));
            Console.WriteLine(ShellColors.ListMinor(
                $"         编码兜底：{p.FallbackEncoding}，工作目录：{p.DefaultWorkingDirectory}", color));
        }

        foreach (var w in warnings) Console.WriteLine(color ? ShellColors.Faint($"  提示：{w}") : $"  提示：{w}");

        Console.WriteLine();
        Console.WriteLine(PtySession.IsSupported
            ? "L3 持久会话：可用（moreshell --l3 <shell>）"
            : "L3 持久会话：当前平台不可用");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            moreshell —— 用 ~ 前缀在 cmd / PowerShell / Git Bash / WSL 之间切换

            用法：
              moreshell                       交互模式
              moreshell --run <shell> <命令>   执行并把颜色画到控制台，退出码透传
              moreshell --json <shell> <命令>  执行并输出解析后的结构化结果
              moreshell --l3 [shell]          持久 PTY 会话（vim/top/less、REPL）
              moreshell --list                列出可用 shell
              moreshell --help                本帮助

            <shell> 可以是 ~ps 或 ps。

            交互模式内建（不进入子进程，因此能跨命令保持状态）：
              cd [路径]    切换当前 shell 的工作目录
              cls / clear  清屏
              pwd          打印当前目录
              exit         退出

            已知边界：
              · 默认行式模式不支持全屏/交互程序（vim、top、REPL）
              · 全屏程序请用 --l3；它要求真实终端，Unix 后端暂不支持运行中改窗口尺寸
              · 命令里带 && | ; > 时不做 cd 拦截，虚拟目录可能与真实目录不同步
              · 行式日志模型，不支持全屏寻址
            """);
    }

    // ==================================================================
    // 小工具
    // ==================================================================

    private static (ShellProfile? Profile, string Command) ResolveInvocation(
        IReadOnlyList<ShellProfile> profiles, string[] tokens)
    {
        var head = tokens[0].Trim();
        if (head.StartsWith('~')) head = head[1..];
        var profile = ShellRegistry.Find(profiles, head);
        var command = tokens.Length > 1 ? string.Join(" ", tokens[1..]) : "";
        return (profile, command);
    }

    private static (string Token, string Remainder) SplitAtFirstSpace(string s)
    {
        s = s.TrimStart();
        int i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return (s[..i], i >= s.Length ? "" : s[i..].TrimStart());
    }
}

/// <summary>
/// <see cref="Console.CancelKeyPress"/> 的最小封装。
/// REPL 里 Ctrl+C 应当<b>只中断当前命令</b>而不是整个程序——否则跑个长命令就得重启整个原型。
/// </summary>
internal static class ConsoleCancel
{
    public static IDisposable Subscribe(Action action)
    {
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;          // 绝不因 Ctrl+C 直接终止进程
            action();
        };
        Console.CancelKeyPress += handler;
        return new Subscription(handler);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ConsoleCancelEventHandler _handler;
        public Subscription(ConsoleCancelEventHandler handler) => _handler = handler;
        public void Dispose() => Console.CancelKeyPress -= _handler;
    }
}
