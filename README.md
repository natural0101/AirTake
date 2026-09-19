# AirTake

Native iPhone camera → local Wi-Fi → Windows disk. Voice comes from the **Fifine / explicitly selected Windows microphone**, not the phone.

**This is a runnable development release, not a claim of field-proven 4K/120 reliability.** The device must expose the exact requested capture format and a hardware HEVC encoder. No silent resolution/FPS fallback and no duplicated frames are used. Sustained recording on a physical iPhone 16 Pro, your Wi-Fi and your microphone still needs validation.

## Скачать и запустить

Готовые сборки находятся в [Releases](https://github.com/natural0101/AirTake/releases). Полная инструкция: [START-RU.md](docs/START-RU.md).

1. Распакуйте **весь** `AirTake-Windows-x64.zip`.
2. Запустите `AirTake.exe` — установка .NET не требуется.
3. Установите подписанное вами приложение **AirTake Camera** на iPhone.
4. Выберите в Windows микрофон Fifine, папку записи и сетевой адаптер.
5. Нажмите «Сохранить / перезапустить».
6. Отсканируйте QR внутри AirTake Camera.
7. Нажмите REC.

**iPhone IPA в релизе не подписан.** Его нужно подписать своим Apple ID/профилем перед установкой. Windows EXE не может заменить нативное приложение камеры на iPhone. Ключи подписи и пароли не входят в репозиторий и не запрашиваются приложением.

## Included

- Windows 10/11 x64 portable desktop, self-contained runtime, all capture/network/audio/disk settings in the GUI.
- iPhone iOS 18+ native SwiftUI application, AVFoundation capture and VideoToolbox hardware-only HEVC Main/SDR.
- Exact 4K or 1080p at 30/60/120 when the main rear camera advertises support.
- 10–200 Mbit/s target bitrate, 1-second GOP, passthrough fragmented MP4 recording.
- Pinned HTTPS pairing, 256-bit bearer token, local network only by the firewall rule supplied in the GUI.
- Bounded persistent retransmission queue: indexed segments, SHA-256, ACK only after PC disk flush and journal update.
- Optional low-rate 640px JPEG preview; it does not replace the original video stream.
- Native Windows microphone WAV, timestamp alignment and explicit audio offset; final AAC 48 kHz.
- Final MP4 assembled without re-encoding the video, original sources retained on errors.
- Actual received/captured counters, dropped-frame counters, measured FPS, queue size, temperature, battery and network-speed test.
- Minimal FFmpeg built from signature-verified official source, including matching source and LGPL license.

## Deliberate limits

- This is a recorder, not a virtual webcam driver or an OBS replacement. No browser/WebRTC capture and no public streaming service.
- The first implementation uses TLS/TCP with acknowledged file fragments, **not QUIC**. It prioritizes recoverable recording over sub-frame preview latency.
- No Dolby Vision, ProRes, Apple Log, HDR, digital 2× camera mode or electronic stabilization.
- Keep the iPhone app foregrounded. Backgrounding, locking, interruptions and critical thermal pressure stop recording; they cannot be made harmless by a Windows EXE.
- Repeated Wi-Fi drops are recoverable only while the bounded queue has capacity. At the limit the camera stops; it does not overwrite unacknowledged footage.
- Crashes can lose the final in-progress MP4 fragment. Completed unacknowledged segments are retained. Phone backups do not include the spool.
- Clock sync uses best-RTT sampling and audio callback timing. It is not sample-accurate hardware clock sync and does not yet correct long-take audio clock drift. Check a clap and adjust the offset for your Fifine driver.
- A single take is limited to 4 hours and the native WAV guard stops before its 4 GB container limit.

## Storage

Approximate video size at **average** bitrate (not a guarantee for the hardware encoder):

| Mbit/s | MB/min | GB/hour |
|---:|---:|---:|
| 100 | 750 | 45 |
| 120 | 900 | 54 |
| 150 | 1125 | 67.5 |

Default iPhone queue: 512 MiB (~35.8 seconds at 120 Mbit/s), plus a bounded emergency allowance for the trailing fragment. The queue is not a second full recording: acknowledged files are deleted. PC finalization needs additional free space roughly equal to the video size; choose a disk with about **2× planned take size** free, plus audio and margin.

## Build / test

Windows with .NET 8 SDK:

```powershell
dotnet run --project tests/AirTake.Tests -c Release -- C:\path\to\ffmpeg.exe
dotnet publish src/AirTake.Desktop -c Release -r win-x64 -o package
```

Place a compatible FFmpeg at `package/tools/ffmpeg.exe`, or select your existing FFmpeg in the Disk tab. The CI build includes it. To reproduce the bundled executable, use `scripts/build-ffmpeg.sh` on Ubuntu with `gcc-mingw-w64-x86-64`, make, curl, tar and GnuPG.

macOS with Xcode and XcodeGen:

```sh
cd ios
xcodegen generate
open AirTakeCamera.xcodeproj
```

Select your Apple signing team and connected iPhone in Xcode, then Run. The CI can compile unsigned device code but cannot create your provisioning profile.

## Tests / evidence

Every published CI release requires the Windows protocol tests, a real FFmpeg HEVC/120fps + PCM→MP4/AAC mux test, launching/rendering the real Windows EXE, and an unsigned iPhone-device SDK build. QA artifacts contain JSON test results, a desktop screenshot and the mux fixture output.

The mux fixture is a tiny synthetic 160×90 / 120fps video: it verifies timing and stream-copy integration, **not 4K camera capture or Wi-Fi throughput**. Physical-device acceptance steps are in [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md).

## Source map

- `src/AirTake.Core` — protocol, durable fragment journal, muxing and recovery.
- `src/AirTake.Desktop` — desktop UI, WASAPI audio, local settings/identity storage.
- `ios/AirTakeCamera` — camera, hardware encoder, segmented writer, spool, TLS client and phone UI.
- `tests/AirTake.Tests` — protocol/security/disk/mux integration checks.
- `.github/workflows/build.yml` — builds, tests and development release artifacts.

Technical reference: [Apple segmented AVAssetWriter](https://developer.apple.com/videos/play/wwdc2020/10011/), [FFmpeg](https://ffmpeg.org/), [NAudio](https://github.com/naudio/NAudio).

AirTake code: MIT. Bundled FFmpeg: LGPL 2.1 or later; matching source and build script accompany the binary. Third-party .NET packages retain their licenses.
