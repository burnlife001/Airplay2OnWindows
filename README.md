# AirPlay Receiver for Windows

[中文文档](README_zh.md)

Mirror your iPhone, iPad or Mac screen to a Windows PC, wirelessly. Your PC shows up in the Apple device's Screen Mirroring / AirPlay list — just pick it, no cable needed.

![Build Status](https://github.com/burnlife001/Airplay2OnWindows/workflows/Build%20and%20Test/badge.svg)

## What it does

- **Screen mirroring** — the whole screen plus its sound is displayed on the PC
- **Audio only** — play music or podcasts through the PC speakers
- **Automatic window** — the picture window opens when mirroring starts and closes when it stops; reconnect as often as you like without restarting
- **Volume sync** — changing volume on the Apple device changes the PC volume
- **Runs in the background** — lives in the system tray, stays out of the taskbar

## Download and install

Get `AirPlay-win-x64-*.zip` from [Releases](https://github.com/burnlife001/Airplay2OnWindows/releases), extract it to any folder, and run `AirPlay.exe`.

- No .NET or media player installation needed — everything is bundled
- Windows may ask for firewall permission on first run: choose **Allow** (private networks are enough)
- Requires 64-bit Windows 10 / 11

## How to use

1. Start `AirPlay.exe`; an AirPlay icon appears in the system tray
2. Make sure the PC and the Apple device are on the **same Wi-Fi** network
3. Start mirroring:
   - iPhone / iPad: Control Center → **Screen Mirroring** → select your PC
   - Mac: menu bar Control Center → **Screen Mirroring** → select your PC
4. The picture appears automatically and closes when mirroring ends

For audio only, tap the AirPlay icon in any audio app on the Apple device and choose your PC as the output device.

## Window and tray

| Action | Result |
|---|---|
| Click the ✕ in the mirroring window | Only hides the picture — mirroring and audio keep running |
| Left-click the tray icon | Bring the mirroring window back |
| Right-click the tray icon → **退出 (Exit)** | Quit the program completely |

## Troubleshooting

**The PC is not listed on the Apple device**

- Make sure both are on the same Wi-Fi and not on a guest network (guest networks usually block device-to-device traffic)
- Check Windows Firewall: Windows Security → Firewall → Allow an app through firewall → tick AirPlay
- If the router has "AP isolation" enabled, devices cannot see each other

**Picture but no sound / no sound at all**

- Check that the correct speaker or headphone is the default playback device
- Make sure no file was missed when extracting: `ffplay.exe`, `ffmpeg.exe` and `libfdk-aac-2.dll` must sit next to `AirPlay.exe`

**Picture frozen**

- Stop mirroring and start it again (iPhones send a full key frame only when mirroring starts, so a picture hidden for a long time may resume on a stale frame)

**Change the name shown on Apple devices**

Open `appsettings_win.json` next to the exe with Notepad, change `Instance`, then restart:

```json
"AirPlayReceiver": {
  "Instance": "My PC",
  "AirTunesPort": 5000,
  "AirPlayPort": 7000
}
```

**Logs**

Logs are written to `%LOCALAPPDATA%\AirPlay\Logs` (paste this into the File Explorer address bar).

## Build from source (developers)

You need the .NET 10 SDK and `ffplay.exe` / `ffmpeg.exe` (in PATH or next to the exe).

```bash
dotnet build AirPlay/AirPlay.csproj -c Release
```

`build-release-debug.ps1` in the repository root offers a menu: build only, build a self-contained zip package, or build the same artifact GitHub CI produces. `libfdk-aac-2.dll` is checked in under `AirPlay/native/` and is copied to the output folder during build.

## Credits

Thanks to these open-source projects:

- [SteeBono/airplayreceiver](https://github.com/SteeBono/airplayreceiver) — original C# AirPlay receiver
- [UxPlay](https://github.com/FDH2/UxPlay) — AirPlay protocol reference
- [mstorsjo/fdk-aac](https://github.com/mstorsjo/fdk-aac) — AAC codec library
- [FFmpeg](https://ffmpeg.org/) — video playback and fallback audio decoding

## Disclaimer

The code in this repository is for learning purposes only. The author takes no responsibility for how it is used. Do not use it to redistribute copyrighted content.

## License

[MIT License](LICENSE)

## Original project

This is a fork of [YimingZhanshen/Airplay2OnWindows](https://github.com/YimingZhanshen/Airplay2OnWindows), with continued fixes and improvements (tray-based UI, embedded mirroring window, audio decoding fixes).

The original implementation comes from [SteeBono/airplayreceiver](https://github.com/SteeBono/airplayreceiver).
