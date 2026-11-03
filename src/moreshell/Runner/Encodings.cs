using System.Globalization;
using System.Text;

namespace moreshell.Runner;

/// <summary>
/// 编码相关的集中入口。
/// .NET Core 默认只内置少数编码，GBK 等本地编码必须注册 <see cref="CodePagesEncodingProvider"/>。
/// </summary>
public static class Encodings
{
    private static int _registered;

    /// <summary>幂等注册代码页提供程序，任何取编码的操作前都应先调用。</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>当前系统的 OEM 码页（中文 Windows 为 936/GBK）。</summary>
    public static int OemCodePage
    {
        get
        {
            EnsureRegistered();
            return CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        }
    }

    /// <summary>当前系统的 OEM 编码，cmd 在管道输出时使用它。</summary>
    public static Encoding Oem
    {
        get
        {
            EnsureRegistered();
            return Encoding.GetEncoding(OemCodePage);
        }
    }

    /// <summary>尝试按指定码页取编码，失败时回退到 OEM。</summary>
    public static Encoding Get(int codePage, Encoding fallback)
    {
        EnsureRegistered();
        try { return Encoding.GetEncoding(codePage); }
        catch (ArgumentException) { return fallback; }
    }
}
