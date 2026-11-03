using System.Diagnostics;
using System.Text;
using moreshell.Shells;

namespace moreshell.Runner;

public sealed record CommandResult
{
    public int ExitCode { get; init; }
    public TimeSpan Duration { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    /// <summary>连进程都没能启动时的错误（路径不存在等）。</summary>
    public string? Failure { get; init; }

    /// <summary>嗅探器最终判定的输出编码（如 <c>utf-8</c> / <c>gb2312</c>），未判定时为 null。</summary>
    public string? StandardOutputEncoding { get; init; }
    public string? StandardErrorEncoding { get; init; }

    public bool Succeeded => Failure is null && !TimedOut && !Cancelled && ExitCode == 0;
}

public sealed record CommandOptions
{
    public required ShellProfile Profile { get; init; }
    public required string Command { get; init; }
    /// <summary>该 shell 体系下的当前目录（Windows 或 Linux 路径，取决于 Profile）。</summary>
    public string? WorkingDirectory { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>stdout 流式回调（可能被多次调用，来自后台线程）。</summary>
    public Action<string>? OnStandardOutput { get; init; }
    /// <summary>stderr 流式回调。</summary>
    public Action<string>? OnStandardError { get; init; }
}

/// <summary>
/// 一次性命令执行器。
///
/// 设计要点：
/// <list type="bullet">
///   <item><b>不用 ConPTY</b>——直接 <see cref="Process"/> 重定向管道，避开最难的 Windows 专属 API；
///        代价是没有 TTY，全屏/交互程序不支持（这是 L2 的既定边界）。</item>
///   <item>读的是 <c>BaseStream</c>（原始字节）而不是 <c>StandardOutput</c>（已解码），
///        因为编码要交给 <see cref="OutputSniffer"/> 判定。</item>
///   <item>stdin 立即关闭，子进程读输入时得到 EOF，避免 <c>pause</c> 之类命令挂死。</item>
///   <item>stdout / stderr 各走一个嗅探器，两个编码互不影响。</item>
///   <item>超时或取消时杀掉<b>整个进程树</b>（wsl.exe 只是启动器，杀它不够）。</item>
/// </list>
/// </summary>
public sealed class CommandRunner
{
    /// <summary>保留的原始输出上限，防止失控命令把内存吃光。</summary>
    private const int MaxSinkChars = 4_000_000;

    public async Task<CommandResult> RunAsync(CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Command))
            throw new ArgumentException("命令不能为空。", nameof(options));

        var tempDir = Path.GetTempPath();
        ShellInvocation invocation;
        try
        {
            invocation = options.Profile.BuildInvocation(options.Command, options.WorkingDirectory, tempDir);
        }
        catch (Exception ex)
        {
            return new CommandResult { Failure = $"构造命令失败：{ex.Message}" };
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await RunCoreAsync(options, invocation, stopwatch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (invocation.TempFile is not null)
                TryDelete(invocation.TempFile);
        }
    }

    private static async Task<CommandResult> RunCoreAsync(
        CommandOptions options, ShellInvocation invocation, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            Arguments = invocation.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // 关闭子进程 stdin，读到 EOF 而不是挂住等键盘
        };

        if (!string.IsNullOrWhiteSpace(invocation.WorkingDirectory))
        {
            if (Directory.Exists(invocation.WorkingDirectory))
                psi.WorkingDirectory = invocation.WorkingDirectory;
        }

        // 鼓励子程序输出颜色：无 TTY 时 git/ls 等会自动关闭颜色
        psi.Environment["FORCE_COLOR"] = "1";
        psi.Environment["CLICOLOR_FORCE"] = "1";
        // 注意：Windows 上默认没有 TERM，必须先查后写，不能用索引器读
        if (!psi.Environment.TryGetValue("TERM", out var term) || string.IsNullOrEmpty(term))
            psi.Environment["TERM"] = "xterm-256color";

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new CommandResult
            {
                Failure = $"启动 {invocation.FileName} 失败：{ex.Message}",
                Duration = stopwatch.Elapsed,
            };
        }

        if (process is null)
        {
            stopwatch.Stop();
            return new CommandResult
            {
                Failure = $"启动 {invocation.FileName} 返回 null。",
                Duration = stopwatch.Elapsed,
            };
        }

        using (process)
        {
            process.StandardInput.Close();

            var outSink = new StringBuilder();
            var errSink = new StringBuilder();
            var fallback = options.Profile.ResolveFallback();

            var outSniffer = new OutputSniffer(fallback);
            var errSniffer = new OutputSniffer(fallback);

            var readOut = ReadLoopAsync(process.StandardOutput.BaseStream, outSniffer, outSink, options.OnStandardOutput);
            var readErr = ReadLoopAsync(process.StandardError.BaseStream, errSniffer, errSink, options.OnStandardError);

            var timedOut = false;
            var cancelled = false;
            string? failure = null;
            var exitCode = -1;

            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (options.Timeout > TimeSpan.Zero) waitCts.CancelAfter(options.Timeout);

                await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
                timedOut = !cancelled;
                KillTree(process);
            }
            catch (Exception ex)
            {
                failure = $"等待进程结束失败：{ex.Message}";
                KillTree(process);
            }

            // 杀掉后管道会 EOF，读取循环自行收尾；这里等它们把残余输出冲干净
            try
            {
                await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure ??= $"读取输出失败：{ex.Message}";
            }

            if (failure is null)
            {
                try { exitCode = process.ExitCode; }
                catch (Exception ex) { failure = $"获取退出码失败：{ex.Message}"; }
            }

            stopwatch.Stop();

            return new CommandResult
            {
                ExitCode = exitCode,
                Duration = stopwatch.Elapsed,
                StandardOutput = Truncate(outSink),
                StandardError = Truncate(errSink),
                StandardOutputEncoding = outSniffer.Chosen?.WebName,
                StandardErrorEncoding = errSniffer.Chosen?.WebName,
                TimedOut = timedOut,
                Cancelled = cancelled,
                Failure = failure,
            };
        }
    }

    /// <summary>
    /// 持续读取原始字节 → 嗅探编码 → 解码文本。
    /// 不传入取消令牌：进程被杀后必须把剩余输出读完，否则会丢尾部日志。
    /// </summary>
    private static async Task ReadLoopAsync(
        Stream stream, OutputSniffer sniffer, StringBuilder sink, Action<string>? callback)
    {
        void Emit(string text)
        {
            if (text.Length == 0) return;
            if (sink.Length < MaxSinkChars) sink.Append(text);
            callback?.Invoke(text);
        }

        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (n <= 0) break;
                sniffer.Push(buffer, n, Emit);
            }
        }
        catch (Exception)
        {
            // 管道被强制关闭：当作正常结束，已读到的内容仍然有效
        }
        finally
        {
            sniffer.Finish(Emit);
        }
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* 可能刚好自己退出了 */ }
        try { process.WaitForExit(3000); } catch { }
    }

    private static string Truncate(StringBuilder sb)
        => sb.Length <= MaxSinkChars ? sb.ToString() : sb.ToString(0, MaxSinkChars);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 临时文件清理失败不影响功能 */ }
    }
}
