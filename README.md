# RTL8239 PoE Command Tool

Realtek RTL8239 PoE 控制器的上位机调试工具。Windows 桌面程序（WPF，.NET Framework 4.8，
C# 语言版本锁 **7.3**）。

把芯片的 12 字节定长主机命令协议（`命令ID | 序列号 | 数据×9 | 校验和`）做成了可点的界面：
命令组装与发送、响应解码、轮询采样与实时曲线、固件下载帧生成、通讯日志。

---

## 构建

用 **VS 自带的 MSBuild**，一条命令：

```bash
"/mnt/d/Programs/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe" \
  'C:\Users\ymz\source\repos\WpfApp1\PoECommandTool.csproj' /t:Rebuild /p:Configuration=Release /nologo /v:minimal
# → PoECommandTool -> ...\bin\Release\PoECommandTool.exe   （0 错误 0 警告）
```

- 工程路径必须写 **Windows 路径**（`C:\...`）；写成 `/mnt/c/...` 会被 MSBuild 当成开关参数。
- **不要**用遗留的 Framework MSBuild（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe`）
  ——它自带的是 C# 5 编译器，连字符串插值都编不过。VS Enterprise 自带的那个不用任何垫片。
- `bin\Release\PoECommandTool.exe` 被另一个实例占用时，构建会卡在最后一步复制（MSB3027）。
  加 `/p:OutputPath='bin\ReviewBuild\'` 换条输出路径即可验证编译。**先确认那是不是用户自己开着的实例**。

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
Serial/          串口会话、协议命令与解析、轮询、设备信息读取、日志存储（纯 C#，可在 Linux 上断言）
Chart/           曲线数据结构、坐标计算、绘图元素（纯 C#，除 ChartPlotElement 外均可断言）
tests/           net10 断言工程（链接上面的源码）+ 渲染探针
tools/           界面自动化脚本
MainWindow*.cs   界面层，一个 partial 一个关注点（串口页 / 轮询 / 图表 / 设备信息 / 命令发送）
```

## 已知约束

- **链路只有 8 位累加和校验，没有认证、没有加密**。它防噪声，不防对手。屏幕上的电压/电流/功率
  是某一台主机对一条未认证字节流的解码结果，**不要把它当作安全联锁使用**。
- 界面里的**裸帧直连**模式可以手工发出任意 12 字节——那是刻意留的逃生口，绕过了界面上的
  安全约束（例如 `0x47` 的清除标志在面板里被固定成 `0x00`，但那个框里你可以自己发别的值）。
