namespace moreshell.Render;

/// <summary>
/// <c>| more</c> / <c>| less</c> 的内置分页器。
///
/// 背景：L2 的一次性子进程没有 TTY，管道末端的真 more/less 既无法暂停也无法读键，
/// 交出去只会一次性倾泻（这正是 <c>cat xx | more</c> “没起作用”的原因）。
/// App 用 <see cref="ShellSimulator.TryExtractPipePager"/> 把这部分拦截下来，
/// 由本类自己分页：每满一屏停下等按键，靠“分页器阻塞读线程 → 子进程 stdout
/// 管道写满自然阻塞”形成背压，观感与真终端里的 more 一致。
///
/// 按键：空格 / Enter / 任意键 = 下一页；<c>q</c> / Esc = 放弃剩余内容（后续输出直通）。
/// Ctrl+C 由外层的取消逻辑接管（命令被终止）。
/// </summary>
public sealed class Pager
{
    private readonly object _consoleLock;
    private readonly CancellationToken _cancellationToken;
    private readonly int _pageSize;
    private int _budget;
    private bool _quit;

    public Pager(object consoleLock, CancellationToken cancellationToken)
    {
        _consoleLock = consoleLock ?? throw new ArgumentNullException(nameof(consoleLock));
        _cancellationToken = cancellationToken;

        int height = 0;
        try { if (!Console.IsOutputRedirected) height = Console.WindowHeight; }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
        _pageSize = height > 4 ? height - 2 : 22;
        _budget = _pageSize;
    }

    /// <summary>一页的行数（窗口高度减去提示行）。</summary>
    public int PageSize => _pageSize;

    /// <summary>剩余是否还在分页中（true = 已被 q 或取消放弃，后续输出直通）。</summary>
    public bool QuitRequested => _quit;

    /// <summary>
    /// 行式闸门：把 <paramref name="text"/> 按行切分交给 <paramref name="next"/> 展示，
    /// 每满 <see cref="PageSize"/> 行暂停等待按键。必须在 stdout 读线程上调用。
    /// </summary>
    public void Gate(string text, Action<string> next)
    {
        if (text.Length == 0) return;

        int pos = 0;
        while (pos < text.Length)
        {
            if (_quit)
            {
                next(text[pos..]);
                return;
            }

            int nl = text.IndexOf('\n', pos);
            if (nl < 0)
            {
                // 页尾剩下的半行照常显示（more 的最后一屏同样如此）
                next(text[pos..]);
                return;
            }

            next(text[pos..(nl + 1)]);
            pos = nl + 1;

            if (--_budget > 0) continue;

            _budget = 0;
            WaitNextPage();
        }
    }

    private void WaitNextPage()
    {
        const string footer = "-- 更多：空格/Enter 下一页，q 退出 --";

        lock (_consoleLock)
        {
            Console.Write("\r" + footer);
        }

        while (true)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                Quit();
                return;
            }

            bool available;
            try { available = !Console.IsInputRedirected && Console.KeyAvailable; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                available = false;
            }

            if (available)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                {
                    Quit();
                    return;
                }

                _budget = _pageSize;
                break;
            }

            Thread.Sleep(40);
        }

        // 用 EL 清掉提示行（比按字符数空格可靠：提示行里有双宽中文，且窗口宽度可能变化）
        lock (_consoleLock)
        {
            Console.Write("\x1b[2K\r");
        }
    }

    private void Quit()
    {
        _quit = true;
        lock (_consoleLock)
        {
            Console.Write("\x1b[2K\r");
        }
    }
}
