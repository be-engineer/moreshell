using System.Text;
using moreshell.Pty;

namespace moreshell.Tests;

/// <summary>
/// 真正的 PTY 集成测试（仅 Linux）：验证子进程确实拿到 tty、输入能进去、输出能出来、
/// 退出码能透传。这些正是 <c>script</c> 包装层最容易出错的点。
/// </summary>
public class PtySessionTests
{
    private static bool CanRun => OperatingSystem.IsLinux() && PtySession.IsSupported;

    [Fact]
    public async Task Session_RunsCommandAndPropagatesExitCode()
    {
        if (!CanRun) return;

        using var session = PtySession.Start(new PtyStartOptions
        {
            FileName = "/bin/sh",
            Arguments = "-c 'printf hello-from-pty; exit 7'",
            Columns = 80,
            Rows = 24,
        });

        string output = await ReadAllAsync(session.Output);
        int exitCode = await WaitForExitAsync(session);

        Assert.Contains("hello-from-pty", output);
        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task Session_ReceivesWrittenInput()
    {
        if (!CanRun) return;

        using var session = PtySession.Start(new PtyStartOptions
        {
            FileName = "/bin/sh",
            Arguments = "-c 'read line; printf got:%s \"$line\"'",
            Columns = 80,
            Rows = 24,
        });

        session.Write("abc\n");

        string output = await ReadAllAsync(session.Output);
        int exitCode = await WaitForExitAsync(session);

        Assert.Contains("got:abc", output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Session_ResizeIsSafe()
    {
        if (!CanRun) return;

        using var session = PtySession.Start(new PtyStartOptions
        {
            FileName = "/bin/sh",
            Arguments = "-c 'exit 0'",
        });

        session.Resize(120, 40);   // Unix 后端为空操作，但必须不抛异常
        Assert.True(session.Columns > 0);
        Assert.True(session.Rows > 0);
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        var sb = new StringBuilder();
        var gate = new object();

        var reader = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read <= 0) break;

                    var chunk = Encoding.UTF8.GetString(buffer, 0, read);
                    lock (gate) sb.Append(chunk);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });

        // 子进程退出后 script 关闭管道，读循环自然结束；超时只是防挂
        await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(15)));
        lock (gate) return sb.ToString();
    }

    private static async Task<int> WaitForExitAsync(PtySession session)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            return await session.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return int.MinValue;
        }
    }
}
