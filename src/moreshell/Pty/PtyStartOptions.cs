namespace moreshell.Pty;

/// <summary>启动一个持久伪终端会话所需的参数。</summary>
public sealed record PtyStartOptions
{
    /// <summary>可执行文件（不走 shell 查找，Unix 下由 sh 解析）。</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// 参数串。Unix 后端会整段交给 <c>sh -c</c> 解析，
    /// 所以内容必须是 shell 语法安全的；Windows 后端按 CreateProcess 规则拼接。
    /// </summary>
    public string Arguments { get; init; } = string.Empty;

    public string? WorkingDirectory { get; init; }

    public int Columns { get; init; } = 80;

    public int Rows { get; init; } = 24;

    /// <summary>追加/覆盖的环境变量；值为 null 表示删除。</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>();
}
