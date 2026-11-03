using System.Diagnostics;
using System.Text;
using moreshell.Input;
using moreshell.Pty;
using moreshell.Render;
using moreshell.Shells;
using moreshell.Vt;

namespace moreshell.Session;

/// <summary>
/// L3 交互会话：把 PTY 输出解析进 <see cref="ScreenBuffer"/>，再整帧画到宿主的真实终端。
///
/// 三个并发循环：
/// <list type="number">
///   <item>输出泵：PTY 字节 → <see cref="TerminalEngine"/>（持 <c>_renderLock</c>）</item>
///   <item>渲染循环：脏屏时生成 ANSI 帧并写 stdout，顺带轮询窗口尺寸</item>
///   <item>输入循环：stdin 原始字节原样转发进 PTY（不再做按键编码，见下）</item>
/// </list>
///
/// 输入为什么不走 <c>Console.ReadKey</c>：原始模式下宿主终端本身就把按键翻译成了
/// VT 序列（Windows 的 ENABLE_VIRTUAL_TERMINAL_INPUT / Unix 的 raw tty），
/// 再做一次“键→序列”编码只会破坏鼠标、粘贴和未知按键，直接转发字节反而最准确。
/// 仅在拿不到 VT 字节输入的旧环境下才退回 <see cref="KeyEncoder"/>。
/// </summary>
public sealed class InteractiveSession
{
    private readonly ShellProfile _profile;
    private readonly ScreenBuffer _screen = new();
    private readonly object _renderLock = new();
    private readonly object _hostLock = new();

    private PtySession? _session;
    private TerminalEngine? _engine;

    public InteractiveSession(ShellProfile profile)
        => _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public ScreenBuffer Screen => _screen;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new InvalidOperationException("L3 交互模式需要真实终端（stdin/stdout 不能被重定向）。");

        if (!PtySession.IsSupported)
            throw new PlatformNotSupportedException("当前平台不支持持久 PTY 会话。");

        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (OperatingSystem.IsWindows())
        {
            // Windows 下让 ReadFile 以 UTF-8 交付键盘输入的字符字节
            try { Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
            catch (IOException) { }
        }

        using var ansi = ConsoleMode.EnableAnsiOutput();
        using var raw = ConsoleMode.EnterRawInput(out bool vtInput);

        int columns = Math.Max(20, SafeWindowSize(vertical: false));
        int rows = Math.Max(5, SafeWindowSize(vertical: true));

        _screen.Resize(columns, rows);
        _engine = new TerminalEngine(_screen, text => _session?.Write(text));

        var invocation = _profile.BuildInteractiveInvocation();
        _session = PtySession.Start(new PtyStartOptions
        {
            FileName = invocation.FileName,
            Arguments = invocation.Arguments,
            WorkingDirectory = invocation.WorkingDirectory,
            Columns = columns,
            Rows = rows,
            Environment = BuildEnvironment(),
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        // 宿主备用屏：必须在渲染循环启动前切过去，否则第一帧会留在宿主主屏上
        WriteHost("\x1b[?1049h\x1b[?25l\x1b[H\x1b[2J");

        int exitCode;
        Task? pump = null;
        Task? render = null;
        try
        {
            pump = PumpAsync(cts.Token);
            render = RenderLoopAsync(cts.Token);

            var input = new Thread(() => InputLoop(vtInput, cts.Token))
            {
                IsBackground = true,
                Name = "moreshell-l3-input",
            };
            input.Start();

            exitCode = await _session.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _session.Kill();
            exitCode = 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;

            // 子进程刚退出时管道里可能还有最后几行，先给输出泵一点时间
            if (pump is not null)
                await Task.WhenAny(pump, Task.Delay(250)).ConfigureAwait(false);

            cts.Cancel();

            if (pump is not null && render is not null)
                await Task.WhenAny(Task.WhenAll(pump, render), Task.Delay(500)).ConfigureAwait(false);

            WriteHost("\x1b[0m\x1b[?2004l\x1b[?25h\x1b[?1049l");
            try { _session.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }

        return exitCode;
    }

    // ------------------------------------------------------------------
    // 输出 → 屏幕
    // ------------------------------------------------------------------

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await _session!.Output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;

                lock (_renderLock)
                    _engine!.Feed(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        bool lastBracketedPaste = false;
        string? lastTitle = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PollResize();

                string? frame = null;
                bool bracketedPaste;
                string? title;

                lock (_renderLock)
                {
                    if (_screen.IsDirty)
                    {
                        frame = AnsiScreenRenderer.Render(_screen);
                        _screen.ClearDirty();
                    }
                    bracketedPaste = _screen.BracketedPaste;
                    title = _screen.Title;
                }

                if (bracketedPaste != lastBracketedPaste)
                {
                    WriteHost(bracketedPaste ? "\x1b[?2004h" : "\x1b[?2004l");
                    lastBracketedPaste = bracketedPaste;
                }

                if (!string.Equals(title, lastTitle, StringComparison.Ordinal))
                {
                    if (title is not null) WriteHost("\x1b]0;" + SanitizeTitle(title) + "\x07");
                    lastTitle = title;
                }

                if (frame is not null) WriteHost(frame);

                await Task.Delay(16, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void PollResize()
    {
        int columns, rows;
        try
        {
            columns = Math.Max(20, Console.WindowWidth);
            rows = Math.Max(5, Console.WindowHeight);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return;
        }

        bool changed;
        lock (_renderLock)
        {
            changed = columns != _screen.Columns || rows != _screen.Rows;
            if (changed) _screen.Resize(columns, rows);
        }

        if (changed) _session?.Resize(columns, rows);
    }

    // ------------------------------------------------------------------
    // 输入 → PTY
    // ------------------------------------------------------------------

    private void InputLoop(bool virtualTerminalInput, CancellationToken cancellationToken)
    {
        try
        {
            if (virtualTerminalInput) ForwardRawInput(cancellationToken);
            else ForwardKeys(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 终端关闭 / 会话结束
        }
    }

    private void ForwardRawInput(CancellationToken cancellationToken)
    {
        using var stdin = Console.OpenStandardInput();
        var buffer = new byte[2048];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read = stdin.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            _session?.Write(buffer.AsSpan(0, read));
        }
    }

    private void ForwardKeys(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true);
            var encoded = KeyEncoder.Encode(key);
            if (encoded is not null) _session?.Write(encoded);
        }
    }

    // ------------------------------------------------------------------
    // 宿主终端
    // ------------------------------------------------------------------

    private void WriteHost(string text)
    {
        lock (_hostLock)
        {
            try
            {
                Console.Out.Write(text);
                Console.Out.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static int SafeWindowSize(bool vertical)
    {
        try { return vertical ? Console.WindowHeight : Console.WindowWidth; }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { return 0; }
    }

    /// <summary>标题来自子进程，必须剔除控制字符，防止提前终止 OSC 或注入别的序列。</summary>
    private static string SanitizeTitle(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (char ch in title)
            if (ch >= 0x20 && ch != 0x7F) sb.Append(ch);
        return sb.ToString();
    }

    private static Dictionary<string, string?> BuildEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "moreshell",
        };

        if (!OperatingSystem.IsWindows()
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG")))
        {
            env["LANG"] = "C.UTF-8";
        }

        return env;
    }
}
