# PoECommandTool

Realtek RTL8239 PoE 控制器的上位机调试工具。Windows 桌面程序（WPF，.NET Framework 4.8，
C# 语言版本锁 **7.3**）。

把芯片的 12 字节定长主机命令协议（`命令ID | 序列号 | 数据×9 | 校验和`）做成了可点的界面：
命令组装与发送、响应解码、轮询采样与实时曲线、固件下载帧生成、通讯日志。

## 三条链路

工具栏最左边选「连接方式」，三条路通到同一个设备调试控制台（`debug#`），
上层的轮询、曲线、设备信息、日志**完全一样**：

| 方式 | 走什么 | 认证 / 加密 | 备注 |
|---|---|---|---|
| 串口 | 本机 COM 口，直连 UART | 无 | 唯一支持**裸帧直连**的方式；固件下载只能在它上面做 |
| Telnet | TCP 到设备（默认 23） | **都没有**，账号与数据明文过网络 | 仅限可信内网 |
| SSH | TCP 到设备（默认 22），SSH.NET | 传输加密 + 主机/用户认证 | 首次连接要确认主机密钥指纹 |

- **口令不落盘**：只在你点「连接」的那一刻从密码框取出、交给传输层。
  本程序没有任何设置持久化，口令不写任何文件；断开**不会**清空口令框
  （同一个设备反复连时不用重复输入），关掉程序即消失。
- 网络链路**强制文本模式**：控制台回的是行文本，没有「裸帧」这回事，
  所以「裸帧直连」会被置灰、固件下载页会被禁用。
- **连接是在后台线程上建立的**（`MainWindow.SerialTab.cs` 的 `OpenSerialAsync`）。
  这不是随手写的：SSH 的主机密钥确认回调**不在调用线程上**抛（实测如此），
  它必须派发回 UI 线程才能弹框；若把连接放回 UI 线程，那个派发会死等 —— **是死锁，不是报错**。
  改动这一段前请先读 `Net/SshTransport.cs` 里 `HostKeyPrompt` 的注释。

---

## 构建

用 **VS 自带的 MSBuild**。加了 SSH.NET 之后（本项目第一个第三方依赖），
`/t:Rebuild` **不再隐式还原包**，所以现在是两条命令：

```bash
# 这两行按你自己的机器改：
#   MSB = VS 自带 MSBuild 的位置（VS 装在哪就在哪）
#   PRJ = 本仓库工程文件的 **Windows 形式** 路径
MSB="/mnt/d/Programs/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe"
PRJ='C:\path\to\PoECommandTool\PoECommandTool.csproj'

"$MSB" "$PRJ" /t:Restore /nologo /v:minimal        # 先还原（首次或改了包版本之后）
"$MSB" "$PRJ" /t:Rebuild /p:Configuration=Release /nologo /v:minimal
# → PoECommandTool -> ...\bin\Release\PoECommandTool.exe   （0 错误 0 警告）
```

- 工程路径必须写 **Windows 路径**（`C:\...`）；写成 `/mnt/c/...` 会被 MSBuild 当成开关参数。
- **不要**用遗留的 Framework MSBuild（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe`）
  ——它自带的是 C# 5 编译器，连字符串插值都编不过。VS Enterprise 自带的那个不用任何垫片。
- `bin\Release\PoECommandTool.exe` 被另一个实例占用时，构建会卡在最后一步复制（MSB3027）。
  加 `/p:OutputPath='bin\ReviewBuild\'` 换条输出路径即可验证编译。**先确认那是不是用户自己开着的实例**。
- 依赖只有 **SSH.NET 2025.1.0** 一个直接包；它会拖来 11 个传递依赖（BouncyCastle.Cryptography、
  System.Memory、System.Formats.Asn1 等，共 12 个 DLL 在 `bin\Release` 里）。
  选 2025.1.0 而不是 2026.0.0，是因为后者在 net48 下会再多一个 `Microsoft.Bcl.Cryptography`。

## 验证

### 断言工程（纯逻辑，覆盖面最大）

`tests/` 是一个 net10 控制台工程，用**相对路径**直接链接仓库里的 `Serial/*.cs`、`Chart/*.cs`
等纯逻辑源码，排除全部 WPF 文件（`MainWindow*`、`*.xaml`、`App`）。所以它是能在 Linux 上跑的真断言。

```bash
dotnet.exe run --project tests/Verify.csproj
# → ALL PASS (756 checks)
```

**注意**：`Serial/*.cs` 会被这个工程以 glob 编进去，所以那些文件里**不能碰 WPF、
也不能依赖 `Environment.NewLine`**（否则 Linux 侧编不过或断言不稳）。

新增测试文件后要手动加进 `tests/Verify.csproj` 的 `<Compile Include>`（工程设了
`EnableDefaultCompileItems=false`，不会自动收）。

### 图表渲染探针（看像素，不只是算坐标）

`tests/probe/` 是一个 `net10.0-windows` + `UseWPF` 的工程，把 `Chart/*.cs` 编进去，
用 `Measure/Arrange` + `RenderTargetBitmap` 把图表**离屏渲染成 PNG**：

```bash
cd tests/probe && dotnet.exe run --project ChartProbe.csproj
# PNG 落在 tests/probe/ 下（已在 .gitignore 里）
```

改过 `ChartPlotElement` / `ChartMath` 的呈现之后跑一次并**看图**——纯逻辑断言只能证明
「坐标算对了」，证不了「画出来是什么样」。

### 界面脚本（`tools/`）

PowerShell + UI Automation，用来驱动和截取真实窗口。

| 脚本 | 用途 |
|---|---|
| `tools/screenshot.ps1` | 逐个页签截图 |
| `tools/shot-serial.ps1` | 串口读写页 |
| `tools/shot-assembly.ps1` | 命令组装页（可选某个命令，并 dump 生成结果文本） |
| `tools/shot-logtab.ps1` | 通讯日志页签 |
| `tools/shot-logwindow.ps1` | 日志窗口生命周期（打开/复用/关闭/进程退出） |

写这类脚本时有三条硬规矩，都是踩过的：

1. **脚本必须纯 ASCII**。PowerShell 5.1 把无 BOM 的 UTF-8 当 GBK 读，中文会让语法解析报错、
   或让字符串比较永远不匹配。中文只放在注释和 `Write-Host` 文案里。
2. **窗口一律按进程号过滤**（UIA 用 `Element.Current.ProcessId`，Win32 用
   `GetWindowThreadProcessId`）。**绝不要用「不是主窗口的那个窗口」这类相对判据**——
   这么写过一次，脚本挑中了用户开着的 VS Code 窗口并把它关掉了。
3. **截图不能按字节比对**（光标闪烁等原因，同一二进制两次运行 PNG 就不同）。要证明
   「行为没变」就取控件文本比对（只读 TextBox 用 `TextPattern.DocumentRange.GetText(-1)`），
   或者用 `git worktree` 检出旧提交、两边各构建一次、跑同一脚本 diff 文本。

## 目录

```
Serial/          传输层抽象（ITransport / 可换芯的 SwitchableTransport / 端点设置）、串口会话、
                 协议命令与解析、轮询、设备信息读取、日志存储（纯 C#，可在 Linux 上断言）
Net/             网络链路：Telnet 协商与传输层、SSH 传输层与读取泵
                 （SshTransport.cs 是**唯一**碰 SSH.NET 的文件；它不进 Linux 断言工程，
                  其余几个文件逐个显式列在 Verify.csproj 里，能断言）
Chart/           曲线数据结构、坐标计算、绘图元素（纯 C#，除 ChartPlotElement 外均可断言）
tests/           net10 断言工程（链接上面的源码）+ 渲染探针
tools/           界面自动化脚本
MainWindow*.cs   界面层，一个 partial 一个关注点（连接页 / 轮询 / 图表 / 设备信息 / 命令发送）
```

`Serial/` 这个名字是历史遗留：它现在放的是**与链路无关**的传输层抽象与会话逻辑，
三种链路共用。改名会牵动每一个文件路径，暂时不动。

## 已知约束

- **RTL8239 命令层本身不认证、不加密**——三条链路都一样。链路**防噪声，不防对手**。
  SSH 另外提供传输加密与主机/用户认证，Telnet 与串口**两者皆无**。屏幕上的电压/电流/功率
  是某一台主机对一条未认证字节流的解码结果，**不要把它当作安全联锁使用**。
- 界面里的**裸帧直连**模式可以手工发出任意 12 字节——那是刻意留的逃生口，绕过了界面上的
  安全约束（例如 `0x47` 的清除标志在面板里被固定成 `0x00`，但那个框里你可以自己发别的值）。
  该模式只在串口链路上可用。
- **网络传输是后加的**：Telnet 那台假设备（回环套接字）有端到端断言，
  但**真机上的 telnetd 还没验过**（协商要求、要不要登录、提示符长什么样都待确认）；
  SSH 只做过「依赖能加载 + 连接被拒时干净报错」的冒烟，**没有连上过真实设备**。
