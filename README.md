# StutterScope

[![Windows build](https://github.com/AdenXie/StutterScope/actions/workflows/windows.yml/badge.svg)](https://github.com/AdenXie/StutterScope/actions/workflows/windows.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Windows 游戏微卡顿记录器。将 PresentMon 逐帧数据与 CPU、GPU、磁盘、内存、后台进程活动对齐；出现明显长帧时自动保存 WPR/ETW 事件级跟踪并就地解码，指出卡顿那一刻游戏主线程是在计算、在等 CPU，还是在等哪个进程或驱动。

游戏前启动，启动时请求管理员授权；关闭记录窗口即停止并生成离线 HTML 报告。没有全局快捷键、游戏注入、叠加层、开机自启或数据上传。

## 下载与运行

**普通使用者请从 [Releases](https://github.com/AdenXie/StutterScope/releases/latest) 下载 `StutterScope-版本-win-x64.zip`。** GitHub 的 Code → Download ZIP 是源码包，需要自行构建。

1. 将整个便携包解压到可写目录。保留 `tools` 子目录和全部随附文件，不要只复制一个 EXE，也不要在 ZIP 内直接运行。
2. 双击 **开始记录.cmd**，在 Windows UAC 弹窗中授权。只有这一种模式：逐帧和系统记录始终开启；系统带有 `wpr.exe` 且磁盘空间足够时，事件级跟踪自动开启，否则自动退回只记录逐帧和系统数据，并在窗口里说明原因。
3. 游戏运行后，确认记录窗口的“帧数”持续增加；事件级跟踪可用时还会显示“深度循环跟踪中”。启动脚本最初的窗口会关闭，保留授权后出现的记录窗口。
4. 玩完关闭记录窗口，或在窗口内按 Enter 停止。后台保存完成后自行退出；深度 ETL 整理可能需要几十秒。
5. 双击 **查看最新报告.cmd**。先看最上方的“诊断线索”，再选择游戏进程/交换链、点击候选卡顿，查看附近的活动。

记录保存在程序旁 `recordings/日期_时间_标识/`。本程序不会自动创建桌面快捷方式，可自行为 CMD 创建快捷方式。ZIP 附带 `SHA256SUMS.txt`，Release 另提供 ZIP 的 SHA-256。

## 兼容性与要求

| 项目 | 范围 |
| --- | --- |
| 主要目标 | Windows 11 x64；已在一台 Ryzen 7 8845H / AMD 核显 + RTX 4060 Laptop 电脑实测 |
| Windows 10 x64 | 尚未验证；深度模式依赖系统 WPR 对自定义配置和命名实例的支持 |
| 不提供支持 | macOS、Linux、32位 Windows；ARM64 原生版本未提供或验证 |
| .NET | .NET Framework 4.8+，Windows 11 通常已内置；不依赖 .NET 8/9、Python、Node.js 或 SDK 来运行 |
| 权限 | 完整逐帧与深度跟踪使用管理员权限；命令行可用普通权限仅采集系统指标 |
| GPU | 不绑定 Intel / AMD / NVIDIA 厂商 API；GPU 指标是否可用取决于驱动和 Windows 性能计数器 |
| 磁盘 | 轻量采集要求保留1 GiB余量；深度跟踪启动要求至少2 GiB余量 |
| 报告 | 使用支持现代 JavaScript 的浏览器，例如当前版本 Edge、Chrome、Firefox |

源码没有固定用户名、电脑名、游戏路径或项目目录。程序按自身所在目录定位资源。**便携并不等于已在所有电脑、游戏、反作弊或精简系统环境验证**；请以帧数、采集状态和日志为准。

.NET 版本说明见 [Microsoft 文档](https://learn.microsoft.com/en-us/dotnet/framework/install/versions-and-dependencies)。PresentMon 控制台参数见 [上游文档](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/README-ConsoleApplication.md)。

## 记录范围

轻量模式：

- PresentMon 逐帧提交间隔、显示变化间隔、GPU 活动；按 PID 和交换链分别分析。
- 系统目标间隔250 ms：总CPU、最忙单核、DPC/中断占比、可用内存、提交量、页面读入/写出、物理磁盘响应和队列、前台应用、供电状态、监控自身开销。实际间隔是等待加采集耗时，会写入日志。
- GPU和进程约每秒采样：最忙物理GPU引擎、专用/共享内存，以及CPU与I/O各前6名进程。进程I/O也包含网络和设备访问。
- 调度探针：每次等待10 ms，记录额外等待≥25 ms的时段；不能单独证明整机冻结。
- 停止时读取会话期间的相关硬件、显卡、存储与电源系统警告/错误，最多200条。

网页候选规则：至少积累30帧后，提交间隔≥12 ms且≥近期平滑基线2倍。加载、切换窗口、限帧变化也可能触发。P99使用0.1 ms向上取整直方图；网页最多展示前台帧最多的16条交换链和每条最长2000个候选，原始CSV保留全部已采集帧。

## 报告里的诊断线索

报告开头按整段记录自动归纳：前台明显卡顿的次数与节奏（是否按固定周期出现）、卡顿帧里显卡是否真的在忙、CPU 是否被限频、最忙核心是否长期满载、内存是否紧张、呈现方式（独占/翻转/窗口化复制合成）、后台持续占用 CPU 的进程，以及每份 ETL 的解码结论。“明显卡顿”指应用在前台、帧间隔 ≥ max(30 ms, 4×近期基线)，3 秒内连续的长帧算一次。

事件级跟踪的启动和保存本身会造成几百毫秒的长帧。落在这些区间内的候选会标为“监控自身干扰”，默认隐藏，也不计入统计。

每一条线索都是测量到的状况，不是已证实的原因。

## 事件级跟踪

默认开启，不需要单独的模式；`StutterScope.exe --light` 或 `Start-Monitor.ps1 -Light` 可以关闭。`DeepTrace.wprp` 额外收集：线程切换/唤醒/CPU采样及相关调用栈、DPC/ISR例程事件和模块加载、DxgKrnl/DWM/DirectComposition/选定Win32k事件、文件和磁盘I/O、磁盘发起栈及硬缺页/硬缺页栈。

配置 **192 MiB内核 + 64 MiB图形循环缓冲**；实际系统/进程开销还会增加。旧事件会被覆盖，实际保留时间随事件量变化，不能保证保留整场游戏或固定一分钟。

自动保存条件：交换链积累≥120帧、提交间隔≥50 ms且≥近期基线4倍、前台PID连续稳定≥3秒、当前跟踪开启≥10秒。超过5秒才送到的旧帧不触发；检测后再等待约1秒，合并这一秒内更长的帧，然后保存ETL。

- 每次保存后冷却10秒，再次开启还有10秒预热；保存、冷却和预热期间不自动生成新文件。基础逐帧/系统记录继续。
- 每轮最多3份ETL。开始新文件前预留512 MiB文件预算，已有文件加预留超过1 GiB则不继续；这不是对WPR合并过程的硬性磁盘配额。
- 关闭窗口时，只有刚检测到、尚未保存的长帧才会触发最后一次保存；没有待保存的长帧就直接丢弃循环缓冲，不再生成一份没有卡顿的ETL。
- 在游戏负载下，256 MiB 缓冲通常只保留最后约 5–15 秒的线程切换事件（每次切换都带调用栈）。自动触发到保存约有 3 秒延迟，所以触发帧本身一般都在保留范围内，更早的卡顿不在。
- 每轮使用独有 WPR `-instancename`，所有启动、状态、保存、取消操作均针对自身实例。
- 保存可能造成后续帧额外波动；报告用浅黄背景标出导出区间。深度模式适合专门复现问题时使用。

保存后程序用系统自带的 ETW 读取接口解码每份 ETL（不需要安装 WPA、符号或其他工具），针对触发帧和同一份跟踪里的其他长帧给出：游戏最忙的几个线程在卡顿期间运行/就绪/阻塞各多久、阻塞时的等待位置和唤醒它的进程（模块链，不含函数名）、卡顿期间各进程占用的核心数与卡顿前 1 秒的对比、按驱动模块汇总的 DPC/ISR 时长、硬缺页次数和等待时间。结果写入 `deep/trace-NN-summary.json` 并显示在报告中。也可以手动解码任意时刻：

```powershell
.\StutterScope.exe --etl .\recordings\会话\deep\trace-01.etl --session .\recordings\会话 --at 121.6 --ms 170 --pid 28040
```

模块级结论足以区分“游戏自己在算”“被抢占”“在等某个进程/驱动”，但**不是函数级根因**；需要函数名时用 Windows Performance Analyzer 打开 `deep/trace-01.etl`。`deep-trace.json` 中开启时间不等于ETL实际保留的起点，丢失事件数未知时不会显示为0。

## 数据与判断边界

所有数据保存在本机。记录可能包含进程名、硬件与驱动版本；深度ETL还可能包含模块/文件路径和线程活动。不要直接把完整诊断目录作为公开 Issue 附件。仓库和发布包均排除真实记录、ETL与测试输出。

图表同期变化是相关线索，不是因果证明。250 ms DPC占比不能测量单次DPC时长；不是每次DPC都有完整栈，私有符号缺失也可能限制解析。当前不读取温度、供电限制原因、原始USB鼠标报告或BIOS/fTPM内部行为。频率下降不能直接当成温度降频。

会话上限2小时或采集日志512 MiB，先到者停止；剩余空间低于1 GiB也停止。报告和ETL额外占空间：一轮记录最多保存3份ETL、约750 MiB，历史记录不自动删除，请定期清理 `recordings`。关机、断电或强杀后台进程仍可能丢失末尾记录。

## 源码构建

使用 Windows 自带的 .NET Framework x64 C# 编译器，不要求安装SDK。构建和下载操作可在普通权限下运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\fetch-presentmon.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\StutterScope.exe --self-test
```

下载脚本固定 PresentMon 2.6.0，校验SHA-256后才放入`tools`，运行时不再下载。第三方二进制不提交到源码仓库，完整便携Release中包含它及MIT许可。

```powershell
# 本地生成干净的便携 ZIP（按文件白名单打包）
powershell -NoProfile -ExecutionPolicy Bypass -File .\Create-Release.ps1

# 普通权限系统/关闭收尾测试；Node.js仅用于可选的报告脚本语法检查
.\tests\smoke.ps1

# 管理员终端：真实WPR和PresentMon并行采集/保存流程自测，会有额外开销
.\StutterScope.exe --admin-self-test
```

管理员自测使用明确标记的合成触发，不代表真实卡顿。云端CI只运行构建、确定性测试、配置校验和打包，不能代替游戏硬件实测。

## 命令行与故障排查

```powershell
# 普通权限：只记录系统
.\StutterScope.exe --capture 15 --system-only
# 以下完整采集应在管理员终端执行
.\StutterScope.exe --capture 60
.\StutterScope.exe --capture 60 --deep --process game.exe   # 定时采集默认不开事件级跟踪，需显式 --deep
# 交互式记录但不开事件级跟踪
.\StutterScope.exe --light
# 从已有会话恢复/重新生成报告
.\StutterScope.exe --report 'C:\path\to\recordings\session'
# 启动前检查，不发起采集或UAC
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start-Monitor.ps1 -CheckOnly
```

- 帧数不增长或出现access denied：逐帧采集启动失败且还没有任何帧时会自动重试最多3次；仍失败再检查管理员授权与`presentmon.log`，并确认真正渲染游戏的进程名。
- 缺少性能计数器：该项会保留为空并记录日志，不把缺失解释成0。精简系统、旧驱动、远程会话或游戏环境可能限制采集。
- 深度失败：检查`deep/deep-trace.json`与`wpr-commands.log`。基础采集仍可继续。
- 关闭后报告未出现：等待后台合并完成；查看最新报告时会提示最近一轮尚未完成，不会悄悄打开上一轮。
- 如果后台进程被强杀，按该轮唯一实例名保存或停止遗留WPR，避免全局取消其他工具的录制：

```powershell
$deep = Get-Content -Raw -LiteralPath '.\recordings\session\deep\deep-trace.json' | ConvertFrom-Json
wpr -stop '.\recordings\session\deep\recovered.etl' -skipPdbGen -instancename $deep.instanceName
# 不保留数据时才使用：wpr -cancel -instancename $deep.instanceName
```

StutterScope采用 [MIT许可证](LICENSE)。第三方组件和上游测试夹具见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。