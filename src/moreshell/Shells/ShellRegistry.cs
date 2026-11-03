using System.Diagnostics;
using System.Text;
using moreshell.Simulator;

namespace moreshell.Shells;

/// <summary>
/// 四个 shell 的 Profile 装配与自动探测。
///
/// 路径不是写死的：Git Bash 在本机位于 <c>D:\app\Git</c>（非默认安装路径），
/// 因此先查候选路径，再从 PATH 里的 git.exe 反推；WSL 发行版从 <c>wsl -l -v</c> 解析。
/// 探测失败也不会抛异常——只是该 Profile 缺席，程序其余部分照常工作。
/// </summary>
public static class ShellRegistry
{
    public sealed record DetectionResult(IReadOnlyList<ShellProfile> Profiles, IReadOnlyList<string> Warnings);

    public static DetectionResult Create()
    {
        var warnings = new List<string>();
        var profiles = new List<ShellProfile>();

        var currentDir = SafeCurrentDirectory();

        // Linux/macOS 上没有 cmd/powershell/wsl，只登记本机的 POSIX shell
        if (!OperatingSystem.IsWindows())
        {
            AddUnixProfiles(profiles, warnings, currentDir);
            return new DetectionResult(profiles, warnings);
        }

        // --- cmd（默认 shell）---
        var cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        profiles.Add(new ShellProfile
        {
            Kind = ShellKind.Cmd,
            Id = "cmd",
            DisplayName = "Command Prompt",
            Executable = File.Exists(cmdExe) ? cmdExe : "cmd.exe",
            FallbackEncoding = EncodingFallback.Oem,
            DefaultWorkingDirectory = currentDir,
            UsesWindowsPath = true,
        });

        // --- PowerShell ---
        var psExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        profiles.Add(new ShellProfile
        {
            Kind = ShellKind.PowerShell,
            Id = "ps",
            DisplayName = "Windows PowerShell",
            Executable = File.Exists(psExe) ? psExe : "powershell.exe",
            Aliases = new[] { "powershell", "pwsh" },
            FallbackEncoding = EncodingFallback.Utf8,
            DefaultWorkingDirectory = currentDir,
            UsesWindowsPath = true,
        });

        // --- Git Bash ---
        var bash = FindGitBash();
        if (bash is null)
        {
            warnings.Add("未找到 Git Bash（尝试过 %PROGRAMFILES%\\Git、PATH 反推等），~bash 不可用。");
        }
        else
        {
            profiles.Add(new ShellProfile
            {
                Kind = ShellKind.Bash,
                Id = "bash",
                DisplayName = "Git Bash",
                Executable = bash,
                Aliases = new[] { "gitbash", "git" },
                FallbackEncoding = EncodingFallback.Utf8,
                DefaultWorkingDirectory = currentDir,
                UsesWindowsPath = true,
            });
        }

        // --- WSL ---
        var distro = FindDefaultWslDistro(out var distroError);
        if (distroError is not null) warnings.Add(distroError);

        // 默认目录与 wsl.exe 的原生行为一致：从 Windows 目录启动时，
        // 发行版里的当前目录就是对应的 /mnt/<盘>/… 映射；映射不了（UNC 等）回退 ~
        profiles.Add(new ShellProfile
        {
            Kind = ShellKind.Wsl,
            Id = "wsl",
            DisplayName = distro is null ? "WSL" : $"WSL ({distro})",
            Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
            Aliases = new[] { "linux" },
            WslDistro = distro,
            FallbackEncoding = EncodingFallback.Utf8,
            DefaultWorkingDirectory = ShellSimulator.WindowsToWslPath(currentDir) ?? "~",
            UsesWindowsPath = false,
        });

        return new DetectionResult(profiles, warnings);
    }

    /// <summary>按 Id 或别名查找，大小写不敏感；找不到返回 null。</summary>
    public static ShellProfile? Find(IReadOnlyList<ShellProfile> profiles, string token)
        => profiles.FirstOrDefault(p => p.MatchesAlias(token));

    // ------------------------------------------------------------------

    /// <summary>Linux/macOS：只探测 bash 与 sh，两种都按 Bash 语义执行。</summary>
    private static void AddUnixProfiles(List<ShellProfile> profiles, List<string> warnings, string currentDir)
    {
        var bash = FindUnixExecutable("bash");
        if (bash is null)
        {
            warnings.Add("未找到 bash，~bash 不可用。");
        }
        else
        {
            profiles.Add(new ShellProfile
            {
                Kind = ShellKind.Bash,
                Id = "bash",
                DisplayName = "Bash",
                Executable = bash,
                Aliases = new[] { "gitbash" },
                FallbackEncoding = EncodingFallback.Utf8,
                DefaultWorkingDirectory = currentDir,
                UsesWindowsPath = false,
            });
        }

        var sh = FindUnixExecutable("sh");
        if (sh is not null && !string.Equals(sh, bash, StringComparison.Ordinal))
        {
            profiles.Add(new ShellProfile
            {
                Kind = ShellKind.Bash,
                Id = "sh",
                DisplayName = "POSIX sh",
                Executable = sh,
                FallbackEncoding = EncodingFallback.Utf8,
                DefaultWorkingDirectory = currentDir,
                UsesWindowsPath = false,
            });
        }
    }

    private static string? FindUnixExecutable(string name)
    {
        foreach (var dir in new[] { "/bin", "/usr/bin", "/usr/local/bin" })
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return FindOnPath(name);
    }

    private static string SafeCurrentDirectory()
    {
        try { return Environment.CurrentDirectory; }
        catch { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
    }

    /// <summary>依次尝试常见安装路径，再从 PATH 中 git.exe 的位置反推 bash。</summary>
    private static string? FindGitBash()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Git", "bin", "bash.exe"));
        if (!string.IsNullOrEmpty(pf86)) candidates.Add(Path.Combine(pf86, "Git", "bin", "bash.exe"));
        candidates.Add(@"D:\app\Git\bin\bash.exe");

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // 从 PATH 反推：git.exe 多半在 <root>\cmd\git.exe，bash 在 <root>\bin\bash.exe
        var git = FindOnPath("git.exe");
        if (git is not null)
        {
            var cmdDir = Path.GetDirectoryName(git);
            var root = cmdDir is null ? null : Path.GetDirectoryName(cmdDir);
            if (root is not null)
            {
                var c = Path.Combine(root, "bin", "bash.exe");
                if (File.Exists(c)) return c;
            }
        }
        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
            }
            catch { /* 路径里可能有非法字符，忽略 */ }
        }
        return null;
    }

    /// <summary>
    /// 用 <c>wsl -l -v</c> 找出默认发行版（前面带 <c>*</c> 的那行）。
    /// 输出为 UTF-16LE，必须显式指定编码，否则中文/表格全乱。
    /// </summary>
    private static string? FindDefaultWslDistro(out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
                Arguments = "-l -v",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode,
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { p.Kill(); }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t', '\uFEFF');
                if (!line.StartsWith('*')) continue;

                // "* Ubuntu   Running   2" → 取第一段
                var name = line[1..].Trim();
                var space = name.IndexOfAny(new[] { ' ', '\t' });
                if (space > 0) name = name[..space];
                if (name.Length > 0) return name;
            }

            if (text.Contains("no installed distributions", StringComparison.OrdinalIgnoreCase))
                error = "WSL 未安装任何发行版。";
            return null;
        }
        catch (Exception ex)
        {
            error = $"探测 WSL 发行版失败：{ex.Message}";
            return null;
        }
    }
}
