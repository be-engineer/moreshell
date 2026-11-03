using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace moreshell.Pty;

/// <summary>
/// Linux 后端：把 util-linux 的 <c>script</c> 当作 PTY 分配器。
///
/// <c>script</c> 会为子进程创建真正的 pty（子进程的 isatty 为真），
/// 再把两端字节桥接到我们的管道上，因此不必自己 forkpty（托管运行时 fork 后不安全）。
///
/// 已知限制：<c>script</c> 不支持运行中改变 pty 尺寸，<see cref="Resize"/> 是空操作；
/// 初始尺寸通过子进程里的 <c>stty</c> 设置。
/// </summary>
public sealed class UnixPtySession : PtySession
{
    /// <summary>util-linux script 的常见位置；找不到则不启用 L3。</summary>
    public static string? ScriptPath { get; } = FindScript();

    private static string? FindScript()
    {
        foreach (var p in new[] { "/usr/bin/script", "/bin/script", "/usr/local/bin/script" })
            if (File.Exists(p)) return p;
        return null;
    }

    private readonly Process _process;
    private readonly Stream _input;
    private readonly object _writeLock = new();
    private readonly object _disposeLock = new();
    private readonly StringBuilder _stderrTail = new();
    private bool _disposed;

    public UnixPtySession(PtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (ScriptPath is null)
            throw new PlatformNotSupportedException("未找到 util-linux 的 script 工具，无法创建 PTY。");

        Columns = Math.Max(1, options.Columns);
        Rows = Math.Max(1, options.Rows);

        var command =
            "stty cols " + Columns.ToString(CultureInfo.InvariantCulture) +
            " rows " + Rows.ToString(CultureInfo.InvariantCulture) + " 2>/dev/null; exec " +
            QuoteSh(options.FileName) +
            (options.Arguments.Length > 0 ? " " + options.Arguments : string.Empty);

        var psi = new ProcessStartInfo
        {
            FileName = ScriptPath!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // script 用 -e 把子进程退出码透传出来，-f 让交互输出不等缓冲
            ArgumentList = { "-q", "-e", "-f", "-c", command, "/dev/null" },
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
            psi.WorkingDirectory = options.WorkingDirectory;

        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["COLORTERM"] = "truecolor";

        foreach (var (key, value) in options.Environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }

        _process = new Process { StartInfo = psi };
        if (!_process.Start())
            throw new InvalidOperationException("无法启动 PTY 会话（script 启动失败）。");

        _input = _process.StandardInput.BaseStream;

        // script 自身的报错（不是子进程的）写到 stderr；必须排空，否则管道满了会卡住 script
        _ = DrainStderrAsync(_process.StandardError.BaseStream);
    }

    public override int Columns { get; }

    public override int Rows { get; }

    public override Stream Output => _process.StandardOutput.BaseStream;

    public override bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    public override int ExitCode => HasExited ? _process.ExitCode : -1;

    /// <summary>script 自己的错误输出（诊断用，通常为空）。</summary>
    public string StderrTail
    {
        get { lock (_stderrTail) return _stderrTail.ToString(); }
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;

        lock (_writeLock)
        {
            if (_disposed || HasExited) return;
            try
            {
                _input.Write(data);
                _input.Flush();   // 交互输入必须立刻到达子进程
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 子进程已退出：丢弃按键即可
            }
        }
    }

    public override void Resize(int columns, int rows)
    {
        // 见类注释：script 不做 TIOCSWINSZ 转发
    }

    public override async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        while (!HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
        return ExitCode;
    }

    public override void Kill()
    {
        if (HasExited) return;
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception ex) { Debug.WriteLine("Kill PTY 失败: " + ex.Message); }
    }

    public override void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _input.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }

        Kill();
        try { _process.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
    }

    private async Task DrainStderrAsync(Stream stderr)
    {
        var buffer = new byte[1024];
        try
        {
            int n;
            while ((n = await stderr.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (_stderrTail)
                {
                    if (_stderrTail.Length < 4096)
                        _stderrTail.Append(Encoding.UTF8.GetString(buffer, 0, n));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>POSIX 单引号：内部单引号用 <c>'\''</c> 跳出再跳回。</summary>
    private static string QuoteSh(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
