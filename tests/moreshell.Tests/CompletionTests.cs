using System.Text;
using moreshell.Input;
using moreshell.Shells;

namespace moreshell.Tests;

/// <summary>L2 行式补全：命令、shell 切换、路径和大小写语义。</summary>
public class CompletionTests : IDisposable
{
    private readonly string _workingDirectory;

    public CompletionTests()
    {
        _workingDirectory = Path.Combine(Path.GetTempPath(), "moreshell-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_workingDirectory, "alpha-dir"));
        File.WriteAllText(Path.Combine(_workingDirectory, "alpha.txt"), "");
        File.WriteAllText(Path.Combine(_workingDirectory, "alpine.txt"), "");
        File.WriteAllText(Path.Combine(_workingDirectory, "beta.txt"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workingDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Path_completion_returns_matching_files_and_directories()
    {
        var set = Complete("cd al", Bash());

        Assert.NotNull(set);
        Assert.Equal(3, set!.TokenStart);
        Assert.Equal(new[] { "alpha-dir/", "alpha.txt", "alpine.txt" }, set.Candidates);
        Assert.Equal("alp", Completion.CommonPrefix(set.Candidates));
    }

    [Fact]
    public void Single_directory_candidate_keeps_trailing_separator()
    {
        var set = Complete("cd alpha-d", Bash());

        Assert.NotNull(set);
        Assert.Equal(new[] { "alpha-dir/" }, set!.Candidates);
        Assert.True(Completion.IsDirectoryCandidate(set.Candidates[0]));
    }

    [Fact]
    public void Path_completion_is_case_sensitive_for_bash()
    {
        Assert.Null(Complete("cd AL", Bash()));
    }

    [Fact]
    public void Path_completion_is_case_insensitive_for_windows_profiles()
    {
        var set = Complete("cd AL", Cmd());

        Assert.NotNull(set);
        Assert.Contains("alpha-dir\\", set!.Candidates);
        Assert.Contains("alpha.txt", set.Candidates);
    }

    [Fact]
    public void Builtin_command_completion_respects_shell_case_rules()
    {
        Assert.Null(Complete("CL", Bash()));

        var set = Complete("CL", Cmd());
        Assert.NotNull(set);
        Assert.Contains("cls", set!.Candidates);
    }

    [Fact]
    public void Shell_switch_completion_includes_aliases()
    {
        var bash = Profile(ShellKind.Bash, "bash", usesWindowsPath: true, "gitbash");
        var powershell = Profile(ShellKind.PowerShell, "ps", usesWindowsPath: true, "powershell", "pwsh");
        var context = new CompletionContext(bash, _workingDirectory, new[] { bash, powershell },
            IncludePathCommands: false);

        var set = Completion.Complete("~p", 2, context);

        Assert.NotNull(set);
        Assert.Equal(new[] { "~powershell", "~ps", "~pwsh" }, set!.Candidates);
    }

    [Fact]
    public void Quoted_path_completion_wraps_candidates()
    {
        var set = Complete("cd \"al", Bash());

        Assert.NotNull(set);
        Assert.Equal(3, set!.TokenStart);
        Assert.Contains("\"alpha-dir/", set.Candidates);
        Assert.Contains("\"alpha.txt\"", set.Candidates);
        Assert.Contains("\"alpine.txt\"", set.Candidates);
    }

    [Fact]
    public void Quoted_windows_path_with_spaces_is_completed()
    {
        Directory.CreateDirectory(Path.Combine(_workingDirectory, "Program Files"));

        var set = Complete("cd \"Pro", Cmd());

        Assert.NotNull(set);
        Assert.Contains("\"Program Files\\", set!.Candidates);
    }

    [Fact]
    public void Target_shell_commands_are_completed_after_switch_prefix()
    {
        var bash = Bash();
        var powershell = Profile(ShellKind.PowerShell, "ps", usesWindowsPath: true, "powershell");
        var cmd = Cmd();
        var context = new CompletionContext(
            bash, _workingDirectory, new[] { bash, powershell, cmd }, IncludePathCommands: false);

        var ps = Completion.Complete("~ps Get-Ch", 10, context);
        Assert.NotNull(ps);
        Assert.Contains("Get-ChildItem", ps!.Candidates);

        var cmdSet = Completion.Complete("~cmd di", 7, context);
        Assert.NotNull(cmdSet);
        Assert.Contains("dir", cmdSet!.Candidates);
    }

    [Fact]
    public void Dot_relative_paths_are_completed()
    {
        var set = Complete("cd ./al", Bash());

        Assert.NotNull(set);
        Assert.Contains("./alpha-dir/", set!.Candidates);
    }

    [Fact]
    public void Wsl_paths_that_do_not_exist_on_the_host_yield_no_candidates()
    {
        var wsl = Profile(ShellKind.Wsl, "wsl", usesWindowsPath: false);
        var context = new CompletionContext(
            wsl, "/home/definitely-not-a-real-path-" + Guid.NewGuid().ToString("N"), new[] { wsl },
            IncludePathCommands: false);

        Assert.Null(Completion.Complete("cd x", 4, context));
    }

    [Fact]
    public void Common_prefix_is_case_insensitive()
    {
        Assert.Equal("Alp", Completion.CommonPrefix(new[] { "Alpha", "ALPINE" }));
        Assert.Equal("", Completion.CommonPrefix(new[] { "alpha", "beta" }));
    }

    private CompletionSet? Complete(string text, ShellProfile shell)
        => Completion.Complete(text, text.Length,
            new CompletionContext(shell, _workingDirectory, new[] { shell }, IncludePathCommands: false));

    private ShellProfile Bash() => Profile(ShellKind.Bash, "bash", usesWindowsPath: false);

    private ShellProfile Cmd() => Profile(ShellKind.Cmd, "cmd", usesWindowsPath: true);

    private static ShellProfile Profile(
        ShellKind kind, string id, bool usesWindowsPath, params string[] aliases) => new()
    {
        Kind = kind,
        Id = id,
        DisplayName = id,
        Executable = "x",
        Aliases = aliases,
        FallbackEncoding = EncodingFallback.Utf8,
        DefaultWorkingDirectory = "/tmp",
        UsesWindowsPath = usesWindowsPath,
    };
}
