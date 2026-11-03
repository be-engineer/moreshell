# moreshell

在 Windows 上，cmd、PowerShell、Git Bash、WSL 各是一个独立终端——想同时用几种 shell，就得在多个窗口之间来回切换、找窗口，每个窗口的目录和历史还互不相通，很麻烦。

moreshell 把这些 shell **融合进一个窗口**：用 `~` 前缀随时切换，在同一个提示符下完成几种 shell 的工作，目录、历史和补全都留在原地。

纯 C# / .NET 10 控制台程序，Windows 与 Linux 均可构建。

## 主要功能

- **多 shell 单窗口**：`~cmd` / `~ps` / `~bash` / `~wsl` 切换当前 shell；`~ps <命令>` 用指定 shell 执行一次，不切换
- **两种运行模式**
  - L2 行式模式（默认）：一次性执行命令，支持 ANSI 颜色、虚拟工作目录、内存历史与 Tab 补全
  - L3 持久会话（`--l3`）：PTY 常驻 shell，支持 vim / top / less / REPL、shell 原生历史与补全
- **彩色输出**：解析 SGR 16 色 / 256 色 / 真彩色；彩色提示符按 shell 区分
- **行编辑与补全**：↑/↓ 历史、Tab 补全（内建命令 / PATH / 当前目录 / `~shell`）、光标移动、常用 Ctrl 快捷键
- **虚拟工作目录**：每个 shell 独立记忆 `cd`，下一条命令以该目录启动；WSL 按 `/mnt/<盘>` 映射
- **内置分页器**：`命令 | more` / `| less` 逐屏显示（空格/Enter 翻页，q 退出），解决无 TTY 时无法分页的问题
- **编码自动嗅探**：GBK / UTF-8 / ASCII 自动判定，中文 Windows 下 cmd 输出不乱码
- **脚本友好**：`--run` 透传退出码，`--json` 输出结构化结果

## 构建与运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

### 安装 .NET 10

| 平台 | 方法 |
|---|---|
| **Windows** | 从[官方下载页](https://dotnet.microsoft.com/download/dotnet/10.0)安装 SDK；或 `winget install Microsoft.DotNet.SDK.10` |
| **Linux** | 官方脚本：`curl -sSL https://dot.net/v1/dotnet-install.sh \| bash -s -- --channel 10.0`；Ubuntu/Debian 也可按[官方 apt 源说明](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu)安装 |
| **macOS** | 官方下载页的 pkg，或 `brew install --cask dotnet-sdk` |

装好后验证：

```bash
dotnet --version    # 应输出 10.x
```

```bash
dotnet build ShellProto.slnx

# L2 交互模式
dotnet run --project src/moreshell/moreshell.csproj

# L3 持久 PTY 会话
dotnet run --project src/moreshell/moreshell.csproj -- --l3 bash
```

Windows 上 L3 依赖 ConPTY（Windows 10 1809+）；Linux 上依赖 util-linux 的 `script`。

不编译也可以直接用仓库内的 Windows 成品：运行 `publish/moreshell.exe`（框架依赖版，目标机需已安装 .NET 10 运行时）。

## 使用方式

```text
moreshell                       交互模式（L2）
moreshell --run <shell> <命令>   一次性执行，颜色实时渲染，退出码透传
moreshell --json <shell> <命令>  一次性执行并输出结构化 JSON
moreshell --l3 [shell]          持久 PTY 会话
moreshell --list                列出探测到的 shell
moreshell --help                查看帮助
```

`<shell>` 可写 `~ps`、`ps`、`~bash`、`~cmd`、`~wsl` 等。

### 使用示例

**切换 shell、用指定 shell 执行一次**

```text
cmd D:\code> ~ps
已切换到 Windows PowerShell
ps D:\code> ~bash git status
（用 Git Bash 执行，当前 shell 仍是 ps）
ps D:\code>
```

**虚拟工作目录（每个 shell 各自记忆）**

```text
cmd D:\code> cd C:\Windows
cmd C:\Windows> dir
（dir 以 C:\Windows 为工作目录启动）
cmd C:\Windows> pwd
C:\Windows
```

**Tab 补全**

```text
ps D:\code> Get-Ch<Tab>
Get-ChildItem
ps D:\code> cd "C:\Prog<Tab>
cd "C:\Program Files
cmd C:\> ~bash ls /tm<Tab>
ls /tmp/
```

**大输出分页（`| more` 由内置分页器接管）**

```text
~wsl cat ../notes.md | more
（第一屏显示后停在 -- 更多：空格/Enter 下一页，q 退出 --）
```

**一次性执行与脚本断言**

```bash
# 透传退出码与颜色
moreshell --run cmd dir
moreshell --run ps "Get-Process | Select-Object -First 3"

# 结构化输出，适合脚本断言（无颜色控制符）
moreshell --json bash "ls /tmp" | jq '.exitCode, (.stdout | length)'
```

**L3 持久会话（全屏程序 / REPL）**

```bash
moreshell --l3 bash     # 里面可运行 vim、top、less、python REPL
moreshell --l3 cmd      # Windows 下用 ConPTY
```

**WSL 路径映射**

```text
cmd D:\develop> ~wsl pwd
/mnt/d/develop               # 与 wsl.exe 原生行为一致
cmd D:\develop> ~wsl cd D:\code
wsl /mnt/d/code> ~wsl ls     # 列出 D:\code 的内容
```

### 内建命令

| 输入 | 行为 |
|---|---|
| `cd [路径]` | 切换当前 shell 的虚拟工作目录（每个 shell 独立） |
| `cls` / `clear` | 清屏 |
| `pwd` | 打印当前目录 |
| `~ps` / `~bash` / `~wsl` / `~cmd` | 切换当前 shell |
| `~ps <命令>` | 用目标 shell 执行一次，不切换当前 shell |
| `?` / `help` | 列出 shell / 打印帮助 |
| `exit` | 退出 |

### 常用按键

| 按键 | 行为 |
|---|---|
| `↑` / `↓` | 翻阅命令历史 |
| `Tab` | 补全当前 token |
| `空格` / `Enter` | 分页器翻页（`more` 模式下） |
| `Ctrl+C` | 放弃当前行（命令执行中则终止命令） |
| `Ctrl+D` / `Ctrl+Z` | 空行上退出程序 |

## 已知限制

- **L2**：一次性进程不保留 shell 内部状态（变量、别名、函数）；命令读 stdin 会立即得到 EOF；全屏程序需用 `--l3`
- **L3**：Linux 运行中改窗口尺寸是空操作（`script` 限制）；鼠标协议、OSC 52 剪贴板、超链接等未实现
- **跨平台**：Linux 上只探测 bash/sh；cmd、PowerShell、WSL 仅 Windows 可用
