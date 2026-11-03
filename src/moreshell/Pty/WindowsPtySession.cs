using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using moreshell.Shells;

namespace moreshell.Pty;

/// <summary>
/// Windows 后端：直接调用 ConPTY（Windows 10 1809+）。
///
/// 流程与官方示例一致：
/// CreatePipe ×2 → CreatePseudoConsole → 关闭 PTY 端 → STARTUPINFOEX 挂上
/// PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE → CreateProcessW。
///
/// 相比 Unix 的 script 包装，这里能真正实现 <see cref="Resize"/>。
/// </summary>
public sealed class WindowsPtySession : PtySession
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint WAIT_OBJECT_0 = 0;

    // ProcThreadAttributeValue(22, FALSE, TRUE, FALSE)
    private static readonly IntPtr ProcThreadAttributePseudoConsole = (IntPtr)0x00020016;

    private readonly IntPtr _hPC;
    private readonly IntPtr _hProcess;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly object _sync = new();
    private int _columns;
    private int _rows;
    private int _exitCode;
    private bool _exitKnown;
    private bool _disposed;

    public WindowsPtySession(PtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int columns = Math.Max(1, options.Columns);
        int rows = Math.Max(1, options.Rows);

        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero, environment = IntPtr.Zero;
        IntPtr hpc = IntPtr.Zero;
        bool attributeListInitialized = false;
        process_information pi = default;
        FileStream? input = null, output = null;

        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0))
                throw Win32();

            if (!CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                throw Win32();

            var size = new COORD { X = (short)columns, Y = (short)rows };
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(size, inputRead, outputWrite, 0, out hpc));

            // ConPTY 已复制这两个句柄，PTY 端可以立即关闭
            CloseHandle(inputRead); inputRead = IntPtr.Zero;
            CloseHandle(outputWrite); outputWrite = IntPtr.Zero;

            input = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
            inputWrite = IntPtr.Zero;
            output = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
            outputRead = IntPtr.Zero;

            IntPtr required = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref required);
            attributeList = Marshal.AllocHGlobal(required);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref required))
                throw Win32();
            attributeListInitialized = true;

            if (!UpdateProcThreadAttribute(
                    attributeList, 0, ProcThreadAttributePseudoConsole, hpc,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Win32();

            environment = BuildEnvironmentBlock(options.Environment);

            var startup = new startup_info_ex
            {
                StartupInfo = new startup_info { cb = Marshal.SizeOf<startup_info_ex>() },
                lpAttributeList = attributeList,
            };

            var commandLine = ShellProfile.QuoteWindowsArg(options.FileName)
                              + (options.Arguments.Length > 0 ? " " + options.Arguments : string.Empty);

            var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? null
                : options.WorkingDirectory;

            if (!CreateProcessW(
                    options.FileName, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    environment, workingDirectory,
                    ref startup, out pi))
                throw Win32();

            CloseHandle(pi.hThread);
            pi.hThread = IntPtr.Zero;

            _columns = columns;
            _rows = rows;
            _hPC = hpc; hpc = IntPtr.Zero;
            _hProcess = pi.hProcess;
            pi.hProcess = IntPtr.Zero;
            _input = input!; input = null;
            _output = output!; output = null;
        }
        catch
        {
            input?.Dispose();
            output?.Dispose();
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc);
            if (inputRead != IntPtr.Zero) CloseHandle(inputRead);
            if (inputWrite != IntPtr.Zero) CloseHandle(inputWrite);
            if (outputRead != IntPtr.Zero) CloseHandle(outputRead);
            if (outputWrite != IntPtr.Zero) CloseHandle(outputWrite);
            throw;
        }
        finally
        {
            if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    public override int Columns => Volatile.Read(ref _columns);

    public override int Rows => Volatile.Read(ref _rows);

    public override Stream Output => _output;

    public override bool HasExited
        => Volatile.Read(ref _exitKnown)
           || WaitForSingleObject(_hProcess, 0) == WAIT_OBJECT_0;

    public override int ExitCode
    {
        get
        {
            if (Volatile.Read(ref _exitKnown)) return Volatile.Read(ref _exitCode);

            if (WaitForSingleObject(_hProcess, 0) != WAIT_OBJECT_0) return -1;
            if (!GetExitCodeProcess(_hProcess, out uint code)) return -1;

            Volatile.Write(ref _exitCode, unchecked((int)code));
            Volatile.Write(ref _exitKnown, true);
            return unchecked((int)code);
        }
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;

        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                _input.Write(data);
                _input.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 子进程已退出
            }
        }
    }

    public override void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, short.MaxValue);
        rows = Math.Clamp(rows, 1, short.MaxValue);

        lock (_sync)
        {
            if (_disposed) return;
            if (columns == _columns && rows == _rows) return;

            var size = new COORD { X = (short)columns, Y = (short)rows };
            if (ResizePseudoConsole(_hPC, size) >= 0)
            {
                Volatile.Write(ref _columns, columns);
                Volatile.Write(ref _rows, rows);
            }
        }
    }

    public override async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        while (!HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
        return ExitCode;
    }

    public override void Kill()
    {
        lock (_sync)
        {
            if (_disposed || HasExited) return;
            try { TerminateProcess(_hProcess, 1); }
            catch (Exception ex) { Debug.WriteLine("终止 PTY 进程失败: " + ex.Message); }
        }
    }

    public override void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _input.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }

        if (!HasExited)
        {
            try { TerminateProcess(_hProcess, 1); }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
            WaitForSingleObject(_hProcess, 2000);
        }

        ClosePseudoConsole(_hPC);
        try { _output.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
        CloseHandle(_hProcess);
    }

    // ------------------------------------------------------------------

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        // CreateProcess 要求环境块按名称排序；'=' 开头的隐藏变量（驱动器当前目录）必须保留
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                variables[key] = value;
        }

        foreach (var (key, value) in overrides)
        {
            if (value is null) variables.Remove(key);
            else variables[key] = value;
        }

        var sb = new StringBuilder();
        foreach (var (key, value) in variables)
            sb.Append(key).Append('=').Append(value).Append('\0');
        sb.Append('\0');

        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private static Exception Win32() => new Win32Exception(Marshal.GetLastWin32Error());

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct startup_info
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct startup_info_ex
    {
        public startup_info StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct process_information
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref startup_info_ex lpStartupInfo,
        out process_information lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
