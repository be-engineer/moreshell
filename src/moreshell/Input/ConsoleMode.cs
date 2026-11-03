using System.Runtime.InteropServices;

namespace moreshell.Input;

/// <summary>
/// 终端模式切换：L3 需要“原始输入”（按键不被行缓冲/回显吞掉）和 Windows 上的 ANSI 输出。
/// 所有方法都返回可释放的作用域，退出时恢复原状。
/// </summary>
public static class ConsoleMode
{
    /// <summary>
    /// 进入原始输入模式。
    /// <paramref name="virtualTerminalInput"/> 为 true 时，stdin 直接产出 VT 字节序列，
    /// 边到边转发即可；为 false 时调用方需退回 ReadKey + 键编码。
    /// </summary>
    public static IDisposable EnterRawInput(out bool virtualTerminalInput)
    {
        if (OperatingSystem.IsWindows()) return WindowsRaw.Enter(out virtualTerminalInput);
        if (OperatingSystem.IsLinux()) return UnixRaw.Enter(out virtualTerminalInput);

        virtualTerminalInput = false;
        return Noop.Instance;
    }

    public static IDisposable EnableAnsiOutput()
    {
        if (OperatingSystem.IsWindows()) return WindowsAnsi.Enter();
        return Noop.Instance;
    }

    /// <summary>
    /// 确保老式 conhost 的 QuickEdit（鼠标选中复制）处于打开状态。
    /// Windows Terminal 原生就支持鼠标选择，此开关对它无意义但无害；
    /// 对 cmd/conhost 窗口则是“选中文本复制”的总开关——曾经被关掉时（历史上
    /// 个别配置工具或早前版本会关），L2 里根本没法用鼠标选中。
    /// </summary>
    public static IDisposable EnsureQuickEditMode()
    {
        if (OperatingSystem.IsWindows()) return WindowsQuickEdit.Enter();
        return Noop.Instance;
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }

    // ------------------------------------------------------------------
    // Windows
    // ------------------------------------------------------------------

    private static class WindowsRaw
    {
        private const int StdInputHandle = -10;
        private const uint EnableProcessedInput = 0x0001;
        private const uint EnableLineInput = 0x0002;
        private const uint EnableEchoInput = 0x0004;
        private const uint EnableQuickEditMode = 0x0040;
        private const uint EnableExtendedFlags = 0x0080;
        private const uint EnableVirtualTerminalInput = 0x0200;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        public static IDisposable Enter(out bool virtualTerminalInput)
        {
            virtualTerminalInput = false;

            var handle = GetStdHandle(StdInputHandle);
            if (!GetConsoleMode(handle, out uint original)) return Noop.Instance;

            // QuickEdit 位必须原样保留：一旦清掉，conhost 里鼠标选中复制就彻底没了
            uint quickEdit = original & EnableQuickEditMode;
            uint rawMode = (original
                & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput))
                | EnableVirtualTerminalInput | EnableExtendedFlags | quickEdit;
            if (!SetConsoleMode(handle, rawMode)) return Noop.Instance;

            virtualTerminalInput = true;
            return new Scope(handle, original);
        }

        private sealed class Scope : IDisposable
        {
            private readonly IntPtr _handle;
            private readonly uint _original;
            private bool _disposed;

            public Scope(IntPtr handle, uint original)
            {
                _handle = handle;
                _original = original;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                SetConsoleMode(_handle, _original);
            }
        }
    }

    private static class WindowsQuickEdit
    {
        private const int StdInputHandle = -10;
        private const uint EnableQuickEditMode = 0x0040;
        private const uint EnableExtendedFlags = 0x0080;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        public static IDisposable Enter()
        {
            var handle = GetStdHandle(StdInputHandle);
            if (!GetConsoleMode(handle, out uint original)) return Noop.Instance;
            if ((original & EnableQuickEditMode) != 0) return Noop.Instance;   // 已经开着

            if (!SetConsoleMode(handle, original | EnableQuickEditMode | EnableExtendedFlags))
                return Noop.Instance;

            return new Scope(handle, original);
        }

        private sealed class Scope : IDisposable
        {
            private readonly IntPtr _handle;
            private readonly uint _original;
            private bool _disposed;

            public Scope(IntPtr handle, uint original)
            {
                _handle = handle;
                _original = original;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                SetConsoleMode(_handle, _original);
            }
        }
    }

    private static class WindowsAnsi
    {
        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        public static IDisposable Enter()
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (!GetConsoleMode(handle, out uint original)) return Noop.Instance;
            if (!SetConsoleMode(handle, original | EnableVirtualTerminalProcessing)) return Noop.Instance;
            return new Scope(handle, original);
        }

        private sealed class Scope : IDisposable
        {
            private readonly IntPtr _handle;
            private readonly uint _original;
            private bool _disposed;

            public Scope(IntPtr handle, uint original)
            {
                _handle = handle;
                _original = original;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                SetConsoleMode(_handle, _original);
            }
        }
    }

    // ------------------------------------------------------------------
    // Linux
    // ------------------------------------------------------------------

    private static class UnixRaw
    {
        private const int StdinFd = 0;
        private const int Tcsanow = 0;

        // termios 标志位（Linux）
        private const uint Icrnl = 0x0100;
        private const uint Ixon = 0x0400;
        private const uint Brkint = 0x0002;
        private const uint Inpck = 0x0010;
        private const uint Istrip = 0x0020;
        private const uint Icanon = 0x0002;
        private const uint Echo = 0x0008;
        private const uint Echoe = 0x0010;
        private const uint Echok = 0x0020;
        private const uint Echonl = 0x0040;
        private const uint Isig = 0x0001;
        private const uint Iexten = 0x8000;

        private const int Vtime = 5;
        private const int Vmin = 6;

        [StructLayout(LayoutKind.Sequential)]
        private struct Termios
        {
            public uint c_iflag;
            public uint c_oflag;
            public uint c_cflag;
            public uint c_lflag;
            public byte c_line;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] c_cc;

            public uint c_ispeed;
            public uint c_ospeed;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int tcgetattr(int fd, ref Termios termios);

        [DllImport("libc", SetLastError = true)]
        private static extern int tcsetattr(int fd, int optionalActions, ref Termios termios);

        public static IDisposable Enter(out bool virtualTerminalInput)
        {
            virtualTerminalInput = false;

            if (Console.IsInputRedirected) return Noop.Instance;

            var original = new Termios { c_cc = new byte[32] };
            if (tcgetattr(StdinFd, ref original) != 0) return Noop.Instance;

            var raw = original;
            raw.c_cc = (byte[])original.c_cc.Clone();
            raw.c_lflag &= ~(Echo | Echoe | Echok | Echonl | Icanon | Isig | Iexten);
            raw.c_iflag &= ~(Icrnl | Ixon | Brkint | Inpck | Istrip);
            raw.c_cc[Vmin] = 1;
            raw.c_cc[Vtime] = 0;

            if (tcsetattr(StdinFd, Tcsanow, ref raw) != 0) return Noop.Instance;

            virtualTerminalInput = true;
            return new Scope(original);
        }

        private sealed class Scope : IDisposable
        {
            private Termios _original;
            private bool _disposed;

            public Scope(Termios original) => _original = original;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                tcsetattr(StdinFd, Tcsanow, ref _original);
            }
        }
    }
}
