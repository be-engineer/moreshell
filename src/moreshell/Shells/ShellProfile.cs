using System.Text;
using moreshell.Runner;

namespace moreshell.Shells;

public enum ShellKind { Cmd, PowerShell, Bash, Wsl }

/// <summary>嗅探器无法判定输出编码时的兜底选择。</summary>
public enum EncodingFallback { Oem, Utf8 }

/// <summary>一次性执行所需的完整进程启动参数。</summary>
public sealed record ShellInvocation
{
    public required string FileName { get; init; }
    public required string Arguments { get; init; }
    /// <summary>子进程启动目录。WSL 用 Linux 路径，故走 <see cref="ShellInvocation.Arguments"/> 内的 cd。</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>进程结束后需要删除的临时脚本。</summary>
    public string? TempFile { get; init; }
}

/// <summary>
/// 一个 shell 的启动配置。四种 shell 的差别全部收敛在这里，
/// 上层的执行、解析、渲染逻辑完全共用。
/// </summary>
public sealed record ShellProfile
{
    public required ShellKind Kind { get; init; }

    /// <summary>短标识，同时是 <c>~</c> 前缀的主拼写：~cmd / ~ps / ~bash / ~wsl</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>可执行文件完整路径（支持配置，Git Bash 在本机并非默认安装路径）。</summary>
    public required string Executable { get; init; }

    /// <summary>额外可识别的前缀别名，如 ~powershell / ~pwsh。</summary>
    public string[] Aliases { get; init; } = Array.Empty<string>();

    /// <summary>WSL 发行版名。</summary>
    public string? WslDistro { get; init; }

    public required EncodingFallback FallbackEncoding { get; init; }

    /// <summary>该 shell 的“虚拟当前目录”初值。</summary>
    public required string DefaultWorkingDirectory { get; init; }

    /// <summary>该 shell 的目录是 Windows 路径还是 Linux 路径。</summary>
    public required bool UsesWindowsPath { get; init; }

    public Encoding ResolveFallback() => FallbackEncoding == EncodingFallback.Oem ? Encodings.Oem : Encoding.UTF8;

    public bool MatchesAlias(string token)
        => string.Equals(token, Id, StringComparison.OrdinalIgnoreCase)
           || Aliases.Any(a => string.Equals(a, token, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    // 构造启动参数
    // ------------------------------------------------------------------

    public ShellInvocation BuildInvocation(string command, string? workingDirectory, string tempDirectory)
        => Kind switch
        {
            ShellKind.Cmd => BuildCmd(command, workingDirectory, tempDirectory),
            ShellKind.PowerShell => BuildPowerShell(command, workingDirectory, tempDirectory),
            ShellKind.Bash => BuildBash(command, workingDirectory),
            ShellKind.Wsl => BuildWsl(command, workingDirectory),
            _ => throw new NotSupportedException($"未知 shell 类型：{Kind}"),
        };

    /// <summary>
    /// 持久会话（L3）的启动参数：不带 <c>-c</c>，让 shell 自己进入交互循环。
    /// </summary>
    public ShellInvocation BuildInteractiveInvocation()
        => Kind switch
        {
            // /d 跳过 AutoRun，/q 关闭命令回显（与打开 cmd 窗口的默认观感一致）
            ShellKind.Cmd => new ShellInvocation
            {
                FileName = Executable,
                Arguments = "/d /q",
                WorkingDirectory = DefaultWorkingDirectory,
            },

            ShellKind.PowerShell => new ShellInvocation
            {
                FileName = Executable,
                Arguments = "-NoLogo",
                WorkingDirectory = DefaultWorkingDirectory,
            },

            // -i 强制交互模式：否则 bash 看到 stdin 不是 tty 会静默退出
            ShellKind.Bash => new ShellInvocation
            {
                FileName = Executable,
                Arguments = "-i",
                WorkingDirectory = DefaultWorkingDirectory,
            },

            // 不带 -e：wsl 会启动发行版的默认登录 shell
            ShellKind.Wsl => new ShellInvocation
            {
                FileName = Executable,
                Arguments = string.IsNullOrWhiteSpace(WslDistro)
                    ? string.Empty
                    : "-d " + QuoteWindowsArg(WslDistro!),
                WorkingDirectory = null,
            },

            _ => throw new NotSupportedException($"未知 shell 类型：{Kind}"),
        };

    /// <summary>
    /// cmd 走临时批处理文件，而不是 <c>cmd /c "…"</c>，原因有二：
    /// 1. cmd 的 /c 引号规则极不稳定（引号会被按条件剥离），临时文件彻底绕开；
    /// 2. 命令里的中文必须以 OEM 码页写入，cmd 才读得对——写进文件可精确控制编码。
    /// </summary>
    private ShellInvocation BuildCmd(string command, string? cwd, string tempDir)
    {
        var temp = Path.Combine(tempDir, $"sp_{Guid.NewGuid():N}.cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        if (!string.IsNullOrWhiteSpace(cwd))
            sb.AppendLine("cd /d \"" + cwd + "\"");
        sb.AppendLine(command);

        // 用 OEM（中文 Windows = GBK）写入，与 cmd 读取批处理时使用的码页一致
        File.WriteAllText(temp, sb.ToString(), Encodings.Oem);

        return new ShellInvocation
        {
            FileName = Executable,
            // 不加 /S：让 cmd 走“引号保留”分支，带空格的临时路径才不会被拆开
            Arguments = "/d /c \"" + temp + "\"",
            WorkingDirectory = null,
            TempFile = temp,
        };
    }

    /// <summary>
    /// PowerShell 用临时 .ps1 + <c>-File</c>：
    /// 1. 同样绕开 <c>-Command</c> 的引号转义问题；
    /// 2. 文件头可以注入编码修正（实测 PS 5.1 默认以 GBK 输出，必须显式改）；
    /// 3. 必须带 UTF-8 BOM，否则 PS 5.1 会把脚本按 ANSI 读，中文全乱。
    /// </summary>
    private ShellInvocation BuildPowerShell(string command, string? cwd, string tempDir)
    {
        var temp = Path.Combine(tempDir, $"sp_{Guid.NewGuid():N}.ps1");
        var sb = new StringBuilder();
        sb.AppendLine("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)");
        sb.AppendLine("$OutputEncoding = [Console]::OutputEncoding");
        if (!string.IsNullOrWhiteSpace(cwd))
            sb.AppendLine("Set-Location -LiteralPath " + QuotePsLiteral(cwd));
        sb.AppendLine(command);

        // BOM 对 PS 5.1 是必需的
        File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return new ShellInvocation
        {
            FileName = Executable,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + temp + "\"",
            WorkingDirectory = cwd,
            TempFile = temp,
        };
    }

    private ShellInvocation BuildBash(string command, string? cwd)
        => new()
        {
            FileName = Executable,
            // 注意：这里必须用 **Windows** 参数转义，不能用 bash 单引号——
            // 单引号只在 POSIX 解析器里才是分组符，Windows argv 解析会把它当普通字符，
            // 导致 `bash -c 'a b'` 被拆成两个参数。
            Arguments = "-c " + QuoteWindowsArg(command),
            WorkingDirectory = cwd,
        };

    private ShellInvocation BuildWsl(string command, string? linuxCwd)
    {
        // 脚本本身（交给 bash 解析的那一层）用 bash 自己的引号规则；
        // 外层再整体做一次 Windows 参数转义，让 wsl.exe 能把它当成**一个**参数转发。
        var script = string.IsNullOrWhiteSpace(linuxCwd)
            ? command
            : "cd " + QuoteLinuxDirectory(linuxCwd) + " && " + command;

        var args = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(WslDistro))
            args.Append("-d ").Append(QuoteWindowsArg(WslDistro)).Append(' ');
        args.Append("-e bash -c ").Append(QuoteWindowsArg(script));

        return new ShellInvocation
        {
            FileName = Executable,
            Arguments = args.ToString(),
            WorkingDirectory = null,
        };
    }

    /// <summary>
    /// 把 Linux 目录写成 bash 能解析的片段。
    /// <c>~</c> 必须<b>不加引号</c>，否则 bash 不做波浪号展开，<c>cd '~'</c> 会报"目录不存在"。
    /// </summary>
    private static string QuoteLinuxDirectory(string path)
    {
        if (path == "~") return "~";
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return "~/" + QuoteBash(path[2..]);      // ~/'带 空格的目录' —— bash 会把两段拼起来
        return QuoteBash(path);
    }

    // ------------------------------------------------------------------
    // 引号转义
    // ------------------------------------------------------------------

    /// <summary>
    /// bash 单引号包裹：最安全的转义方式，内部单引号用 <c>'\''</c> 跳出再跳回。
    /// 单引号内除单引号本身外一切都保持字面量，因此任意命令都无需再考虑 $、`、\ 等。
    /// </summary>
    public static string QuoteBash(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>PowerShell 单引号字面量：内部单引号翻倍。</summary>
    public static string QuotePsLiteral(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Windows 命令行参数标准转义（供 CreateProcess 用）。</summary>
    public static string QuoteWindowsArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0) return arg;

        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
        int backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { backslashes++; continue; }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(ch);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
