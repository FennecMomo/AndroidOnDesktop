# Android 窗口

让普通 APK 在本机 Android 环境中执行，并通过独立的 Windows 窗口使用。第一版使用 .NET 10 / WPF 开发 Windows 管理程序，组合 Google Android Emulator、ADB 与 scrcpy；不依赖连接手机。

当前版本为 0.1.4 原型。Android 兼容能力来自 Android 系统镜像，并非把 APK 转换成 Windows EXE，也不是自行重写 Android 内核。

## 0.1.4 图标与本机部署

程序、窗口和桌面快捷方式使用绿色安卓头像与桌面窗口组合图标，提供 16–256 像素 ICO、PNG 和可编辑 SVG。`tools/Build-Icon.ps1` 可重新生成资源。

独立部署时将发布文件直接放在安装目录根部，同级放置 `runtime` 与 `data`，无需保留工程源码或解决方案文件。迁移需要同步修改 `data/settings.json` 中的工具路径、AVD 索引和配置中的旧路径。系统安装的 .NET / 虚拟化组件仍使用本机现有环境，不属于目录内便携组件。本机计划部署到 `D:\Tools\AndroidOnDesktop`；部署验证记录随该目录保存。

## 0.1.3 退出行为修复

- 正常管理程序连接到已经运行的实例时，会核对它是否属于本项目 `runtime/avd` 中配置的本地虚拟设备；匹配的实例纳入本次程序的停止与退出管理。
- 点击“停止 Android”或正常关闭管理程序时，关闭本程序创建的应用窗口，并正常停止对应的本项目 Android 实例，保留已经安装的应用和数据。
- 操作进行中关闭管理程序，会先取消当前操作，等待清理完成后自动关闭，无需再次点击关闭。
- 其他 AVD 和实体设备不会因关闭此程序而被停止。现有只连接用途的验证命令继续保留只连接行为。

以上针对正常关闭流程；任务管理器强制结束主程序、主程序崩溃等情况下的后台进程自动回收不在本次修复范围内。必要检查及未测试项见 `docs/待测试清单.md`。

本机已完成必要编译、正常启动后退出、接管已有本项目 AVD 后退出，以及启动中取消后自动退出检查；样例应用窗口和对应 Android 进程均已清理。只连接验证模式退出后保留已有实例。记录位于 `artifacts/verification/release-0.1.3/`，未开展全面业务或流畅度测试。

## 0.1.2 性能调整

- 默认应用显示尺寸从 `1000x1600` 调整为 `720x1280`，像素数量减少 42.4%；视频输出限制为 30 FPS，使用 H.264、4 Mbps。
- Android 内存默认设为 3072 MB。安装脚本只对新建 AVD 写入此值，已有 AVD 配置与数据保留；通过程序新启动 Android 时，会传入当前运行配置的内存值。
- “运行配置”增加图形加速、安卓内存和帧率上限。图形加速的通用默认值仍为 `auto`；本机已验证 `host` 模式能使用 NVIDIA GeForce RTX 4070 Ti，而原先的实际 GLES 渲染器是 Google SwiftShader 软件渲染。
- 图形和内存修改需要重启 Android 才生效；分辨率、帧率和视频码率作用于新打开的应用窗口。固定虚拟分辨率、90 秒应用启动等待与崩溃检测继续保留。

本次必要诊断还观察到：原 2560 MB Android 实例的 zram 中约有 1.4 GB 已换出数据，先前应用窗口运行区间的软件媒体编解码进程与番茄进程均有明显 CPU 负载；Windows 当时约剩 4.47 GiB 可用内存，因此先采用 3072 MB，而非直接大幅增加分配。重启后已确认 GLES 路径为 NVIDIA，启动阶段交换量为 0；这不代表持续运行番茄后的交换量或帧率已经达到某个数值。

诊断原始记录位于 `artifacts/performance/20261103-163554/`，0.1.2 最终构建与必要启动检查位于 `artifacts/verification/release-0.1.2/`。本次没有进行受控的相同滑动负载前后对比，不能把像素数量减少等同为速度提高 42.4%，也不能承诺 ARM 转译开销已消除。

## 0.1.1 更新

- 默认运行环境切换为 Android 16 / API 36、Google APIs、x86_64，默认 AVD 为 `AndroidOnDesktop_API36`，端口为 `5584`。
- 应用首次进入可见、已绘制状态的等待时间由 30 秒延长到 90 秒；后台 Android 系统启动等待仍为 4 分钟。
- 启动后持续监测 Android 应用崩溃，显示明确错误、记录原因，并关闭该会话创建的空白窗口。
- 普通管理程序会话日志保存到 `data/logs`，可通过“打开日志”或错误提示中的“查看日志”读取。
- 默认保持固定 Android 分辨率；调整 Windows 窗口时缩放画面，避免反复重建 Android 显示配置。
- 打开管理程序时自动连接配置中已运行的 Android 并刷新应用列表；点击“启动 Android”也会优先复用同名已就绪实例。
- 安装脚本使用独占锁防止并发安装，并检查系统镜像关键文件是否齐全。

原 Android 15 / API 35 和 Android 11 / API 30 的 AVD 及数据目录保留。各 AVD 的应用、登录和缓存分别保存，切换默认环境不会自动迁移这些数据。

## 使用

1. 运行 `artifacts/app/AndroidOnDesktop.exe`。本机已安装 .NET 10 Windows Desktop Runtime。
2. 首次使用点击“安装运行组件”，按 Google 官方安装器提示确认 SDK 许可并完成下载。组件保存在项目内 `runtime` 目录，不修改 Unity SDK，也不修改系统环境变量。
3. 安装完成后，点击“运行配置 → 自动查找 → 保存配置”，再点击“检测环境”。默认虚拟设备为 `AndroidOnDesktop_API36`，端口为 `5584`。
4. 点击“启动 Android”，等待后台 Android 完成启动。
5. 点击“安装 APK”或拖入一个 APK，选中应用，点击“打开应用窗口”。双击应用也可以打开。
6. 在应用窗口中使用鼠标与键盘。窗口可以移动、调整大小和关闭。正常关闭管理程序会关闭它创建的应用窗口，并停止它启动或已确认属于本项目的 Android 实例；数据保存在虚拟设备中。

第一版应用列表按包名显示，只列出具有启动入口的第三方应用。按钮“打开 Android 设置”可打开系统设置窗口。

性能配置可在“运行配置”中调整。当前通用默认值为 `720x1280`、30 FPS、3072 MB、图形 `auto`；本机按用户要求保存为 8192 MB，并使用已经验证的图形 `host` 配置。需要回到自动图形选择时，选择“自动兼容”并在重启 Android 后生效。

本次开发电脑已安装 Android 16 环境。番茄小说 `com.dragon.read` 7.4.3.32 在此环境已显示欢迎与个人信息说明页；用户自行确认后，后台检测到首页 `MainFragmentActivity` 可见、已完成绘制，当次采集没有新的应用崩溃记录。登录、阅读、书架、音频及长时稳定性仍待用户安排验证。

0.1.1 已完成新版窗口加载、自动连接现有 API 36 实例、读取两个已安装应用的必要检查；番茄启动后进入可见且完成绘制的状态，继续观察 20 秒未发现新崩溃，随后正常关闭本次检查窗口。在 0.1.1 / 0.1.2 中，已有 Android 实例仅被连接，不随新管理程序退出；0.1.3 已针对本项目本地 AVD 调整这一行为，上述记录保留为历史验证证据。

0.1.1 最终构建、程序启动和环境核对证据保存在 `artifacts/verification/release-0.1.1/`。此前 `artifacts/verification/runtime-smoke.json` 等文件记录的是 0.1.0 时点的必要检查，不代表新版全部业务验收。

如果环境检测报告缺少虚拟化加速，请按报告检查 BIOS 虚拟化和 Windows“虚拟机监控程序平台”（Windows Hypervisor Platform）。程序不会自行修改 Windows 功能或重启电脑。

## 已实现

- 运行组件自动发现与配置保存。
- Android Emulator 后台启动、就绪等待、取消、停止；正常退出时清理本程序管理的本项目 Android 实例。
- 明确选择设备；已有设备也可通过 ADB 使用。
- APK 安装、刷新第三方可启动应用、包名筛选。
- 每个应用使用独立虚拟显示器和 Windows 窗口，支持鼠标键盘。
- Android 11 以上默认由首个应用窗口播放设备音频，其他同时打开的窗口关闭音频，避免重复播放。音频拥有者关闭后，新开的窗口可以接续；已经打开的静音窗口需重新打开。
- 实际组件日志、加速诊断、超时及错误提示。
- 应用启动等待与持续崩溃监测：Android 11 以上按启动前退出记录基线识别新的 Java / 原生崩溃；Android 10 使用崩溃日志回退。确认失败后显示原因并关闭对应会话窗口。

## 兼容范围

默认系统镜像为 Android 16 / API 36，Google APIs，x86_64。scrcpy 固定为 5.0.1。独立虚拟显示器最低需要 Android 10 / API 29 及 scrcpy 3；实际功能会按工具支持情况启用。

番茄小说 7.4.3.32 在原 Android 15 环境出现 ARM64 原生执行/转译路径崩溃，在 Android 11 对照环境出现 ICU 文本函数转译错误；固定分辨率未解决这两种内核兼容故障。默认切换 Android 16 是针对本机已观察结果作出的选择，不代表所有 ARM APK 都已兼容。

普通 Java/Kotlin APK 和提供 x86_64 库的 APK 是第一批目标。仅提供 ARM 原生库的应用、依赖特定硬件或厂商服务的应用、受保护视频、Google Play 登录、游戏及外部显示器适配，需要逐个 APK 验证。Google APIs 镜像带有 Google 服务组件，不包含 Play Store。

多个应用窗口共享同一 Android 系统和设备音频；窗口不等于独立 Android 实例。系统如何复用同一个应用的任务取决于应用自身行为。关闭窗口时默认销毁该虚拟显示器中的任务，APK 及持久化数据保留。

## 开发

```powershell
dotnet build AndroidOnDesktop.slnx -c Release -m:1 -p:MSBuildEnableWorkloadResolver=false -p:UseSharedCompilation=false
dotnet publish src/AndroidOnDesktop.Desktop/AndroidOnDesktop.Desktop.csproj -c Release -o artifacts/app -m:1 -p:MSBuildEnableWorkloadResolver=false -p:UseSharedCompilation=false
```

源码结构：

- `src/AndroidOnDesktop.Core`：工具发现、配置、进程和 Android 运行管理。
- `src/AndroidOnDesktop.Desktop`：WPF 界面。
- `tools/Install-Runtime.ps1`：项目内运行环境安装。
- `tools/Build-SmokeApk.ps1`、`samples/SmokeApp`：普通 Android 冒烟 APK 及其源码。
- `docs/待测试清单.md`：未测试项目和验证边界。
- `artifacts/verification`：本机必要构建与冒烟证据。

运行组件也可在终端安装：

```powershell
.\tools\Install-Runtime.ps1
```

安装脚本默认下载 API 36。需要保留其他独立环境时，可以显式传入 `-ApiLevel 35` 或 `-ApiLevel 30`；不要让不同系统版本共用同一 AVD 数据目录。

`-NonInteractive` 只复用已有许可，缺少许可时停止。仅在用户已阅读并同意 SDK 许可后才可使用 `-AcceptAndroidSdkLicense`。安装脚本需要从 Google 和 GitHub 官方源下载组件，首次占用数 GB。

## 第三方来源

- [Android Emulator 命令行](https://developer.android.com/studio/run/emulator-commandline)
- [硬件加速配置](https://developer.android.com/studio/run/emulator-acceleration)
- [Android SDK 许可](https://developer.android.com/studio/terms)
- [scrcpy 官方项目及许可](https://github.com/Genymobile/scrcpy)
- [scrcpy 虚拟显示器](https://github.com/Genymobile/scrcpy/blob/master/doc/virtual-display.md)
- [scrcpy 5.0.1 下载](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1)

本项目不将 Google 系统镜像打入应用压缩包，运行组件由使用者通过官方来源在本机安装。第三方组件保留各自许可。
