# LightSync

A Windows app for ELK-BLEDOM Bluetooth LED strips (the ones controlled by the **Lotus Lantern** phone app). It mirrors your screen onto the strip in real time for movies, YouTube and games. It also covers the phone app's basics: power, solid colors, brightness, and built-in effects with speed control.

## Running it

Download it from [Releases](https://github.com/wasey-rao/LightSync/releases/latest):

- **`LightSync-…-standalone.exe`** runs on any 64-bit Windows 10/11 PC with nothing else to install. Pick this one if you're unsure.
- **`LightSync-….exe`** is smaller but needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

Put the exe in a permanent folder (for example `Documents\LightSync`) before turning on "Start with Windows", because that setting remembers where the exe is.

1. **Close Lotus Lantern on your phone**, or turn the phone's Bluetooth off. The strip accepts one connection at a time and stops advertising while the phone holds it.
2. Start LightSync. It connects automatically to the configured strip and the status card in the bottom-left turns green. To set up a different strip, use **Settings → Scan**.
3. On **Screen Sync**, press **Start Screen Sync**. With more than one screen, LightSync first shows what each screen is displaying right now. Click the one you're watching. Tick **Remember** to skip that question next time; you can change screens later with **Choose** on the Source card.

Closing the window keeps LightSync running in the system tray. Left-click the tray icon to reopen it. Right-click it to start/stop sync, turn the strip on/off, or exit.

To have it start with Windows, go to **Settings → Startup & tray** and tick **Start LightSync when I sign in to Windows**. Do this from `dist\LightSync.exe`, since that is the path that gets registered. Also tick **Start hidden in the system tray** if you don't want the window to appear. If **Resume screen sync on start** is on, sync starts by itself whenever it was running when you last quit.

## Screen sync settings

| Setting | What it does |
| --- | --- |
| **Display** | Which screen to follow. **Choose** shows a live snapshot of every screen. **Ask which display every time** shows that picker whenever you start sync by hand. |
| **Color mode** | *Vibrant* (default) favors colorful pixels so the strip shows a scene's mood instead of grey mush. *Average* is the literal mean color. *Dominant* locks onto the single strongest hue. |
| **Screen area** | *Whole screen*, *Edges* (ambilight style, for a strip behind the monitor/TV) or *Center*. |
| **Capture method** | *Automatic* uses DirectX desktop duplication, which works with games, hardware-accelerated video and HDR desktops, and switches to GDI if DirectX can't capture the screen. |
| **Ignore black bars** | Crops letterbox/pillarbox bars so movies don't dim the strip. |
| **Update rate** | Color updates per second sent to the strip (default 30). |
| **Smoothing** | Fades between colors. Raise it if the strip flickers in fast scenes. |
| **Saturation / Brightness boost / Contrast** | Tune how vivid, bright and contrasty the strip looks. Contrast (gamma) above 1 keeps dark scenes dark. |
| **Minimum brightness** | Keeps a faint glow during black scenes. |
| **White balance** | If whites look blue or green on the strip, lower that channel. |

The big preview swatch shows exactly what is sent to the strip, along with the capture method, frame rate and processing time.

## Troubleshooting

- **"Strip not found" / "Couldn't reach the strip"**: the phone is probably still connected. Close Lotus Lantern, or toggle the phone's Bluetooth off, then click **Connect**. Power-cycling the strip also frees it.
- **Laggy or dropping connection**: this PC sees the strip at about −84 dBm, which is weak. Moving the PC/strip closer or using a USB Bluetooth 5 dongle helps. LightSync reconnects automatically when the link drops.
- **The strip goes dark on Netflix / Prime / Disney+ in a browser**: DRM-protected video is blanked for every screen-capture app. Nothing can capture it.
- **Exclusive-fullscreen games**: DirectX capture usually works. If a game shows black, switch it to borderless/windowed fullscreen.
- **HDR screens**: supported. The preview says "DirectX HDR", and colors are scaled using Windows' "SDR content brightness" setting. If the strip looks too bright or too dim with HDR on, adjust **Brightness boost**.
- **Using the phone again**: click **Disconnect** (or exit LightSync) so the phone can connect.
- **Log**: Settings → Activity log, or `%APPDATA%\LightSync\lightsync.log`. Settings are stored in `%APPDATA%\LightSync\settings.json`.

## Building from source

Requires the .NET 10 SDK.

```powershell
dotnet run --project LightSync          # debug run
.\publish.ps1                           # -> dist\LightSync.exe
```

## How it works

```
DXGI desktop duplication ──► GPU mip-chain downscale (≈120×67 px) ──► color extraction
   (GDI StretchBlt fallback)    8-bit, 10-bit or HDR (scRGB,           (vibrant / average / dominant,
                                 scaled by the SDR white level)         letterbox crop, region)
        ──► saturation · brightness · gamma · smoothing · white balance
        ──► coalescing BLE writer (only the newest color, ≤ 1 per frame) ──► strip
```

Processing takes about 2–3 ms per frame. Only about 8,000 pixels ever reach the CPU.

### ELK-BLEDOM protocol

Service `FFF0`, write characteristic `FFF3` (write-without-response). Every command is 9 bytes:

| Command | Bytes |
| --- | --- |
| Power on | `7E 00 04 F0 00 01 FF 00 EF` |
| Power off | `7E 00 04 00 00 00 FF 00 EF` |
| Color | `7E 00 05 03 RR GG BB 00 EF` |
| Brightness (0–100) | `7E 00 01 BB 00 00 00 00 EF` |
| Effect | `7E 00 03 CC 03 00 00 00 EF` (`CC` = `0x87`–`0x9C`) |
| Effect speed (0–100) | `7E 00 02 SS 00 00 00 00 EF` |

### Project layout

```
LightSync/
  Ble/        ElkProtocol (commands), ElkBledomDevice (connection, coalescing writer, auto-reconnect)
  Capture/    DxgiCapture, GpuDownsampler (8-bit / 10-bit / HDR), GdiCapture, DisplayConfig, monitor enumeration
  Sync/       ColorExtractor, ColorPipeline, ScreenSyncEngine, SyncSettings
  Controls/   LabeledSlider
  MainWindow           UI, tray icon
  MonitorPickerWindow  visual "which screen?" picker
publish.ps1   single-file release build into dist\
```
