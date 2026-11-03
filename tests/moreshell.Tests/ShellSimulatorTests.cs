using moreshell.Shells;
using moreshell.Simulator;

namespace moreshell.Tests;

/// <summary>
/// 虚拟目录模拟层测试。
///
/// 之所以要测这一层：L2 每条命令都是一次性的子进程，进程一退目录就没了。
/// 如果 <c>cd</c> 不在这里拦截，前一条命令的 <c>cd</c> 会静默失效，
/// 而这类"看起来没反应"的问题最难被发现。
/// </summary>
public sealed class ShellSimulatorTests : IDisposable
{
    // ---------- 构造 ----------

    /// <summary>测试用的真实目录，由构造函数创建、析构时删除——否则 <c>cd</c> 会因"目录不存在"被拒。</summary>
    private static readonly string WindowsDir =
        Path.Combine(Path.GetTempPath(), "moreshellTests");

    /// <summary>跨平台的现有目录：Windows 上取 System 目录，Linux 上取 /tmp。</summary>
    private static readonly string ExistingDir =
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/tmp";

    public ShellSimulatorTests() => Directory.CreateDirectory(WindowsDir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(WindowsDir)) Directory.Delete(WindowsDir, recursive: true);
        }
        catch (IOException) { }              // 被占用时不强求清理
        catch (UnauthorizedAccessException) { }
    }

    private static ShellProfile Profile(ShellKind kind, string id, string defaultCwd, bool windowsPath)
        => new()
        {
            Kind = kind,
            Id = id,
            DisplayName = id,
            Executable = id + ".exe",
            FallbackEncoding = EncodingFallback.Utf8,
            DefaultWorkingDirectory = defaultCwd,
            UsesWindowsPath = windowsPath,
        };

    private static ShellProfile Cmd(string? cwd = null)
        => Profile(ShellKind.Cmd, "cmd", cwd ?? ExistingDir, windowsPath: true);

    private static ShellProfile Ps(string? cwd = null)
        => Profile(ShellKind.PowerShell, "ps", cwd ?? ExistingDir, windowsPath: true);

    private static ShellProfile Bash(string? cwd = null)
        => Profile(ShellKind.Bash, "bash", cwd ?? ExistingDir, windowsPath: true);

    private static ShellProfile Wsl(string? cwd = null)
        => Profile(ShellKind.Wsl, "wsl", cwd ?? "~", windowsPath: false);

    private static ShellSimulator NewSimulator(params ShellProfile[] profiles) => new(profiles);

    // ---------- Windows 路径下的 cd ----------

    [Fact]
    public void CdAbsolute_UpdatesDirectory()
    {
        var p = Cmd();
        var sim = NewSimulator(p);

        sim.Intercept(p, "cd " + WindowsDir);

        Assert.Equal(WindowsDir, sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void CdRelative_GoesDeeper()
    {
        // 默认目录要落在 Temp 下，否则相对路径 "moreshellTests" 会因不存在而被拒
        var p = Cmd(Path.GetTempPath());
        var sim = NewSimulator(p);

        sim.Intercept(p, "cd moreshellTests");

        Assert.Equal(WindowsDir, sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void CdDotDot_GoesUp()
    {
        var p = Cmd();
        var sim = NewSimulator(p);
        sim.Intercept(p, "cd " + WindowsDir);

        sim.Intercept(p, "cd ..");

        Assert.Equal(Path.GetDirectoryName(WindowsDir), sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void CdNonexistent_ReportsErrorAndKeepsDirectory()
    {
        var p = Cmd();
        var sim = NewSimulator(p);

        var r = sim.Intercept(p, @"cd C:\definitely\not\exists-" + Guid.NewGuid().ToString("N"));

        Assert.True(r.Handled);
        Assert.NotNull(r.Message);
        Assert.Equal(ExistingDir, sim.GetWorkingDirectory(p));   // 目录不能被改坏
    }

    [Fact]
    public void CdQuotedPath_WithSpaces_IsHandled()
    {
        var p = Cmd();
        var sim = NewSimulator(p);
        var target = Path.Combine(Path.GetTempPath(), "moreshell tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);

        try
        {
            var r = sim.Intercept(p, "cd \"" + target + "\"");

            Assert.True(r.Handled);
            Assert.Null(r.Message);
            Assert.Equal(target, sim.GetWorkingDirectory(p));
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    /// <summary>cmd 的 <c>cd /d</c> 是换盘符开关，必须先剥掉再解析。</summary>
    [Fact]
    public void CdWithDriveSwitch_IsSupported()
    {
        var p = Cmd();
        var sim = NewSimulator(p);

        sim.Intercept(p, "cd /d " + WindowsDir);

        Assert.Equal(WindowsDir, sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void CdBareCommand_ReportsCurrentDirectory()
    {
        var p = Cmd();
        var sim = NewSimulator(p);

        var r = sim.Intercept(p, "cd");

        Assert.True(r.Handled);
        Assert.Equal(ExistingDir, r.Message);
    }

    /// <summary>cmd 里 <c>%VAR%</c> 会被 cmd 自己展开，我们也照做，否则 cd 到字面量目录名。</summary>
    [Fact]
    public void CdPercentVariable_IsExpanded()
    {
        const string name = "MORESHELL_TEST_CD_TARGET";
        Environment.SetEnvironmentVariable(name, WindowsDir);

        try
        {
            var p = Cmd();
            var sim = NewSimulator(p);

            sim.Intercept(p, "cd %" + name + "%");

            Assert.Equal(Path.GetFullPath(WindowsDir), sim.GetWorkingDirectory(p));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void CdBareDriveLetter_IsDeferredToRealShell()
    {
        var p = Cmd();
        var sim = NewSimulator(p);

        var r = sim.Intercept(p, "cd D:");

        Assert.False(r.Handled);      // `cd D:` 只切换不改目录，语义复杂，交给真 shell
    }

    /// <summary>cmd 里 ~ 不是家目录，只是个普通字符名，因此报"找不到路径"才是保真的。</summary>
    [Fact]
    public void CdTilde_InCmd_IsNotFound()
    {
        var p = Cmd();
        var sim = NewSimulator(p);

        var r = sim.Intercept(p, "cd ~");

        Assert.True(r.Handled);
        Assert.NotNull(r.Message);
        Assert.Equal(ExistingDir, sim.GetWorkingDirectory(p));
    }

    /// <summary>而 PowerShell 里 ~ 就是家目录，必须展开。</summary>
    [Fact]
    public void CdTilde_InPowerShell_ExpandsToUserProfile()
    {
        var p = Ps();
        var sim = NewSimulator(p);

        sim.Intercept(p, "cd ~");

        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void CdTildeSubPath_InPowerShell_Expands()
    {
        var p = Ps();
        var sim = NewSimulator(p);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var name = "moreshell-tilde-" + Guid.NewGuid().ToString("N");
        var target = Path.Combine(home, name);
        Directory.CreateDirectory(target);

        try
        {
            sim.Intercept(p, "cd ~/" + name);     // 用真实存在的子目录，否则会被"目录不存在"拒掉

            Assert.Equal(target, sim.GetWorkingDirectory(p));
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    // ---------- 不拦截的情况 ----------

    [Theory]
    [InlineData("cd a && dir")]
    [InlineData("cd a || dir")]
    [InlineData("cd a; dir")]
    [InlineData("cd a | more")]
    [InlineData("cd a > out.txt")]
    [InlineData("cd a < in.txt")]
    public void CommandsWithControlOperators_AreNotIntercepted(string command)
    {
        var p = Cmd();
        var sim = NewSimulator(p);

        var r = sim.Intercept(p, command);

        Assert.False(r.Handled);
        Assert.Equal(ExistingDir, sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void UnknownCommand_IsPassedThrough()
    {
        var p = Cmd();
        var sim = NewSimulator(p);

        Assert.False(sim.Intercept(p, "dir /b").Handled);
    }

    [Fact]
    public void EmptyCommand_IsHandledSilently()
    {
        var p = Cmd();
        var r = NewSimulator(p).Intercept(p, "   ");

        Assert.True(r.Handled);
        Assert.Null(r.Message);
    }

    // ---------- cls / pwd / exit ----------

    [Fact]
    public void Cls_RequestsScreenClear()
    {
        var p = Cmd();
        Assert.True(NewSimulator(p).Intercept(p, "cls").ClearScreen);
    }

    [Fact]
    public void Clear_InBash_RequestsScreenClear()
    {
        var p = Bash();
        Assert.True(NewSimulator(p).Intercept(p, "clear").ClearScreen);
    }

    [Fact]
    public void ClearHost_InPowerShell_RequestsScreenClear()
    {
        var p = Ps();
        Assert.True(NewSimulator(p).Intercept(p, "Clear-Host").ClearScreen);
    }

    [Fact]
    public void Exit_RequestsTermination()
    {
        var p = Cmd();
        Assert.True(NewSimulator(p).Intercept(p, "exit").ShouldExit);
    }

    [Fact]
    public void Pwd_InPowerShell_ReportsDirectory()
    {
        var p = Ps();
        var r = NewSimulator(p).Intercept(p, "pwd");

        Assert.True(r.Handled);
        Assert.Equal(ExistingDir, r.Message);
    }

    /// <summary>cmd 本来就没有 pwd，但本机 PATH 上装了 Git 的 pwd.exe，交给真 shell 更保真。</summary>
    [Fact]
    public void Pwd_InCmd_IsPassedThrough()
    {
        var p = Cmd();
        Assert.False(NewSimulator(p).Intercept(p, "pwd").Handled);
    }

    // ---------- POSIX 路径 ----------

    [Fact]
    public void CdAbsolute_InWsl_UsesLinuxPath()
    {
        var p = Wsl();
        var sim = NewSimulator(p);

        sim.Intercept(p, "cd /usr");

        Assert.Equal("/usr", sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void CdRelative_InWsl_JoinsWithSlash()
    {
        var p = Wsl();
        var sim = NewSimulator(p);
        sim.Intercept(p, "cd /usr");

        sim.Intercept(p, "cd local");

        Assert.Equal("/usr/local", sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void CdDotDot_InWsl_Normalizes()
    {
        var p = Wsl();
        var sim = NewSimulator(p);
        sim.Intercept(p, "cd /usr/local/bin");

        sim.Intercept(p, "cd ..");

        Assert.Equal("/usr/local", sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void CdDotDot_AtWslRoot_StaysAtRoot()
    {
        var p = Wsl();
        var sim = NewSimulator(p);
        sim.Intercept(p, "cd /");

        sim.Intercept(p, "cd ..");

        Assert.Equal("/", sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void CdDotDot_AtWslHome_StaysAtHome()
    {
        var p = Wsl();
        var sim = NewSimulator(p);

        sim.Intercept(p, "cd ..");

        Assert.Equal("~", sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void BareCd_InWslGoesToHome()
    {
        var p = Wsl();
        var sim = NewSimulator(p);
        sim.Intercept(p, "cd /usr");

        sim.Intercept(p, "cd");

        Assert.Equal("~", sim.GetWorkingDirectory(p));
    }

    /// <summary>每个 shell 各自维护目录，互不影响——这是"一个窗口四个 shell"的前提。</summary>
    [Fact]
    public void Directories_AreIsolatedPerShell()
    {
        var cmd = Cmd();
        var wsl = Wsl();
        var sim = NewSimulator(cmd, wsl);

        sim.Intercept(cmd, "cd " + WindowsDir);
        sim.Intercept(wsl, "cd /usr");

        Assert.Equal(WindowsDir, sim.GetWorkingDirectory(cmd));
        Assert.Equal("/usr", sim.GetWorkingDirectory(wsl));
    }

    // ---------- 纯函数 ----------

    [Theory]
    [InlineData("/d", "D:\\")]
    [InlineData("/d/", "D:\\")]
    [InlineData("/d/app", "D:\\app")]
    [InlineData("/c/Users/me", "C:\\Users\\me")]
    [InlineData("/Z/x", "Z:\\x")]
    public void MsysToWindows_ConvertsDrivePaths(string input, string expected)
        => Assert.Equal(expected, ShellSimulator.MsysToWindows(input));

    [Theory]
    [InlineData("/home/leon")]
    [InlineData("/usr/local")]
    [InlineData("relative")]
    [InlineData("")]
    public void MsysToWindows_ReturnsNullWhenNotConvertible(string input)
        => Assert.Null(ShellSimulator.MsysToWindows(input));

    [Theory]
    [InlineData("~", "~")]
    [InlineData("~/a", "~/a")]
    [InlineData("~/a/../b", "~/b")]
    [InlineData("~/a/./b", "~/a/b")]
    [InlineData("~/..", "~")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/a/./b//c", "/a/b/c")]
    [InlineData("/../a", "/a")]
    [InlineData("/", "/")]
    public void NormalizeLinux_HandlesBothRoots(string input, string expected)
        => Assert.Equal(expected, ShellSimulator.NormalizeLinux(input));

    // ---------- WSL：Windows 路径 ⇄ /mnt/<盘> 映射 ----------

    [Theory]
    [InlineData("D:\\code\\proj", "/mnt/d/code/proj")]
    [InlineData("d:/code/proj", "/mnt/d/code/proj")]
    [InlineData("C:\\", "/mnt/c")]
    [InlineData("C:", "/mnt/c")]
    [InlineData("Z:\\a b\\c", "/mnt/z/a b/c")]
    public void WindowsToWslPath_ConvertsDrivePaths(string input, string expected)
        => Assert.Equal(expected, ShellSimulator.WindowsToWslPath(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\\\\server\\share\\x")]   // UNC 不映射
    [InlineData("relative")]
    [InlineData("/usr/local")]             // 已经是 Linux 路径
    public void WindowsToWslPath_ReturnsNullWhenNotConvertible(string input)
        => Assert.Null(ShellSimulator.WindowsToWslPath(input));

    [Fact]
    public void Wsl_Cd_AcceptsWindowsStylePath()
    {
        var p = Wsl();
        var sim = NewSimulator(p);

        sim.Intercept(p, "cd D:\\code\\proj");

        Assert.Equal("/mnt/d/code/proj", sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void Wsl_Cd_RelativeFromTranslatedDefault()
    {
        // wsl.exe 的默认行为：启动目录映射为 /mnt/<盘>/…，相对路径从这里算
        var p = Wsl("/mnt/d/develop/projects/other/console_win");
        var sim = NewSimulator(p);

        sim.Intercept(p, "cd ..");

        Assert.Equal("/mnt/d/develop/projects/other", sim.GetWorkingDirectory(p));
    }

    /// <summary>WSL 目录在发行版文件系统里，宿主侧无法校验，哪怕不存在也不能拦下来。</summary>
    [Fact]
    public void Wsl_Cd_DoesNotValidateNonexistentPath()
    {
        var p = Wsl();
        var sim = NewSimulator(p);

        var outcome = sim.Intercept(p, "cd /definitely/not/there");

        Assert.True(outcome.Handled);
        Assert.Null(outcome.Message);
        Assert.Equal("/definitely/not/there", sim.GetWorkingDirectory(p));
    }

    [Fact]
    public void Wsl_Pwd_TranslatedDefault()
    {
        var p = Wsl("/mnt/d/work");
        var sim = NewSimulator(p);

        var outcome = sim.Intercept(p, "pwd");

        Assert.True(outcome.Handled);
        Assert.Equal("/mnt/d/work", outcome.Message);
    }

    // ---------- 管道分页器（| more / | less） ----------

    [Theory]
    [InlineData("cat a.md | more", "cat a.md")]
    [InlineData("cat a.md|more", "cat a.md")]
    [InlineData("cat a.md | less", "cat a.md")]
    [InlineData("dir | more", "dir")]
    [InlineData("cat a.md | MORE", "cat a.md")]
    [InlineData("cat a.md | more -5", "cat a.md")]
    [InlineData("ps aux | grep x | more", "ps aux | grep x")]
    public void TryExtractPipePager_StripsTrailingPager(string input, string expectedInner)
    {
        Assert.True(ShellSimulator.TryExtractPipePager(input, out var inner));
        Assert.Equal(expectedInner, inner);
    }

    [Theory]
    [InlineData("cat a.md")]
    [InlineData("echo \"a|more\"")]          // 管道在引号里，是打印内容
    [InlineData("ps aux | grep core")]       // 末端不是分页器
    [InlineData("a | ")]                     // 管道后为空
    [InlineData("")]
    public void TryExtractPipePager_LeavesCommandAlone(string input)
    {
        Assert.False(ShellSimulator.TryExtractPipePager(input, out var inner));
        Assert.Equal(input, inner);
    }

    // ---------- 本地 POSIX shell 的 cd 校验 ----------

    [Fact]
    public void LinuxBash_Cd_RejectsNonexistentDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;   // 该语义只在真实 POSIX 宿主上成立

        var p = Profile(ShellKind.Bash, "bash", "/tmp", windowsPath: false);
        var sim = NewSimulator(p);

        var outcome = sim.Intercept(p, "cd /definitely/not/there");

        Assert.Equal("/tmp", sim.GetWorkingDirectory(p));     // 目录没变
        Assert.Contains("没有那个文件或目录", outcome.Message ?? "");
    }
}
