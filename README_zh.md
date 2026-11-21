# AirPlay 投屏接收器（Windows）

[English](README.md)

把 iPhone、iPad 或 Mac 的画面和声音，无线投到 Windows 电脑上。电脑会出现在苹果设备的「屏幕镜像 / AirPlay」列表里，选中即可，无需数据线。

![构建状态](https://github.com/burnlife001/Airplay2OnWindows/workflows/Build%20and%20Test/badge.svg)

## 能做什么

- **屏幕镜像**：手机或 Mac 的整个屏幕投到电脑上，声音同步过来
- **只放声音**：音乐、播客直接通过电脑音箱播放
- **自动出画面**：开始投屏时窗口自动打开，断开后自动关闭，反复投屏不用重启程序
- **音量跟随**：在苹果设备上按音量键，电脑输出音量同步变化
- **后台常驻**：程序缩在托盘，不占任务栏

## 下载与安装

在 [Releases](https://github.com/burnlife001/Airplay2OnWindows/releases) 下载 `AirPlay-win-x64-*.zip`，解压到任意文件夹，双击 `AirPlay.exe` 即可。

- 不需要另外安装 .NET 或播放器，压缩包里已经带齐
- 首次运行 Windows 可能弹出防火墙提示，请选择**允许访问**（专用网络即可）
- 要求 64 位 Windows 10 / 11

## 使用方法

1. 启动 `AirPlay.exe`，右下角托盘出现 AirPlay 图标
2. 确保电脑和苹果设备连在**同一个 Wi-Fi**
3. 投屏：
   - iPhone / iPad：控制中心 →「屏幕镜像」→ 选择你的电脑
   - Mac：菜单栏控制中心 →「屏幕镜像」→ 选择你的电脑
4. 画面会自动弹出，投屏结束后窗口自动关闭

只放音乐时，在苹果设备的音乐 App 里点 AirPlay 图标，把输出设备选成电脑即可。

## 窗口和托盘

| 操作 | 结果 |
|---|---|
| 点投屏窗口右上角的 ✕ | 只隐藏画面，投屏和声音继续，不会断开 |
| 左键单击托盘图标 | 重新显示投屏画面 |
| 右键托盘图标 →「退出」 | 彻底关闭程序 |

## 常见问题

**苹果设备里找不到这台电脑**

- 确认在同一 Wi-Fi，且不是访客网络（访客网络通常禁止设备互访）
- 检查 Windows 防火墙是否放行了本程序；被拦时在「Windows 安全中心 → 防火墙 → 允许应用通过防火墙」里勾选 AirPlay
- 路由器如果开了「AP 隔离」，设备之间看不到彼此

**有画面没声音 / 没有声音**

- 确认系统默认播放设备是你要用的音箱或耳机
- 解压时不要漏文件，`ffplay.exe`、`ffmpeg.exe`、`libfdk-aac-2.dll` 必须和 `AirPlay.exe` 在同一目录

**画面卡住不动**

- 断开投屏后重新连一次（iPhone 只在投屏开始时发送一次关键画面，中途长时间隐藏窗口再显示可能卡在旧画面）

**想改电脑显示的名字**

默认显示成「机器名:用户名」（例如 `ez390:yg`），由程序自动获取，无需配置。

想换成别的名字，用记事本打开同目录下的 `appsettings_win.json`，把 `Instance` 改成固定文字后重启程序：

```json
"AirPlayReceiver": {
  "Instance": "我的电脑",
  "AirTunesPort": 5000,
  "AirPlayPort": 7000
}
```

两个占位符 `{MachineName}` / `{UserName}` 可自由组合，例如 `"{UserName} 的电脑"`。

**出问题要看日志**

日志在 `%LOCALAPPDATA%\AirPlay\Logs`（把这段路径粘到资源管理器地址栏即可打开）。

## 从源码编译（开发者）

需要 .NET 10 SDK，以及 `ffplay.exe` / `ffmpeg.exe`（放在 PATH 或程序目录）。

```bash
dotnet build AirPlay/AirPlay.csproj -c Release
```

仓库根目录的 `build-release-debug.ps1` 提供菜单：只编译、生成自包含 zip 分发包、生成与 GitHub CI 一致的制品包。`libfdk-aac-2.dll` 已随源码放在 `AirPlay/native/`，编译时会自动复制到输出目录。

## 致谢

感谢这些开源项目：

- [SteeBono/airplayreceiver](https://github.com/SteeBono/airplayreceiver) — 原始 C# AirPlay 接收端
- [UxPlay](https://github.com/FDH2/UxPlay) — AirPlay 协议参考
- [mstorsjo/fdk-aac](https://github.com/mstorsjo/fdk-aac) — AAC 编解码库
- [FFmpeg](https://ffmpeg.org/) — 视频播放与备用音频解码

## 免责声明

本仓库代码仅供学习交流，作者不对使用后果承担责任。请勿用于商业盗播等用途。

## 许可证

[MIT 许可证](LICENSE)

## 原仓库

本项目是 [YimingZhanshen/Airplay2OnWindows](https://github.com/YimingZhanshen/Airplay2OnWindows) 的 fork 版本，在其基础上继续修复和改进（托盘化界面、内嵌投屏窗口、音频解码修复等）。

更早的原始实现来自 [SteeBono/airplayreceiver](https://github.com/SteeBono/airplayreceiver)。
