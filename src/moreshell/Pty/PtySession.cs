using System.Text;

namespace moreshell.Pty;

/// <summary>
/// 一个持久伪终端会话：子进程认为自己接在真终端上，
/// 因此全屏程序（vim/top/less）、REPL、行编辑都能正常工作。
///
/// 上层只关心三件事：读 <see cref="Output"/>、写 <see cref="Write(ReadOnlySpan{byte})"/>、
/// 收到窗口变化时调 <see cref="Resize"/>。平台差异全部藏在子类里。
/// </summary>
public abstract class PtySession : IDisposable
{
    public abstract int Columns { get; }
    public abstract int Rows { get; }

    /// <summary>子进程输出字节流（未经解码）。读到 EOF 表示会话结束。</summary>
    public abstract Stream Output { get; }

    public abstract bool HasExited { get; }

    /// <summary>仅当 <see cref="HasExited"/> 为 true 时有效。</summary>
    public abstract int ExitCode { get; }

    /// <summary>把原始字节送进终端。调用方负责把按键编码成字节。</summary>
    public abstract void Write(ReadOnlySpan<byte> data);

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    /// <summary>通知子进程终端尺寸变化。Unix 后端目前无法在运行中调整。</summary>
    public abstract void Resize(int columns, int rows);

    public abstract Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    public abstract void Kill();

    /// <summary>当前平台是否具备启动 PTY 的条件。</summary>
    public static bool IsSupported
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
           || (OperatingSystem.IsLinux() && UnixPtySession.ScriptPath is not null);

    public static PtySession Start(PtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (OperatingSystem.IsWindows())
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                throw new PlatformNotSupportedException("ConPTY 需要 Windows 10 1809（build 17763）或更高版本。");
            return new WindowsPtySession(options);
        }

        if (OperatingSystem.IsLinux())
        {
            if (UnixPtySession.ScriptPath is null)
                throw new PlatformNotSupportedException("未找到 util-linux 的 script 工具，无法创建 PTY。");
            return new UnixPtySession(options);
        }

        throw new PlatformNotSupportedException("当前平台不支持持久 PTY 会话。");
    }

    public virtual void Dispose()
    {
    }
}
