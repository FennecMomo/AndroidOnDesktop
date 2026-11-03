# Android 窗口示例

独立的普通 Android APK，用于本项目必要的安装、独立窗口启动和基本输入冒烟检查。

- 包名：`org.androidondesktop.smoke`
- 启动 Activity：`org.androidondesktop.smoke.MainActivity`
- Android 10 / API 29 起可安装，targetSdk 为 34。
- 原生 Java Activity，包含标题、单行输入框和点击计数按钮。
- 无应用权限、无网络访问、无外部代码依赖。

在项目根目录运行：

```powershell
.\tools\Build-SmokeApk.ps1
```

脚本优先从环境变量和现有 ADB 位置寻找 Android SDK，复用 Unity SDK 时会寻找相邻的 OpenJDK。也可显式传入 `-SdkRoot`、`-JdkRoot`、`-AndroidJar` 和 `-OutputApk`。需要已有 Android SDK build-tools、Android platform 和 JDK，不会下载组件或修改 SDK。

默认输出为 `artifacts\SmokeApp.apk`，构建证据为 `artifacts\SmokeApp.build.json`。本地调试签名保存在 `artifacts\smoke-debug.keystore`，只用于此示例。

构建脚本执行 Java 编译、DEX 转换、APK 资源链接、对齐、签名和签名/对齐检查。编译成功不等同于设备运行通过；设备运行状态以项目实际冒烟记录为准。
