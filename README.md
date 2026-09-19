# AirTake

**iPhone camera → encrypted Wi-Fi → Windows disk. Fifine audio stays on the PC.**

Native Windows desktop receiver + native iOS camera companion. No cloud, no OBS, no virtual webcam.

## Download and start

1. Open [Releases](https://github.com/natural0101/AirTake/releases).
2. Download `AirTake-Windows-x64.zip` and extract **the entire ZIP**.
3. Launch `AirTake.exe`. The .NET runtime is included. Keep the `tools` folder next to the executable.
4. Install and sign the iPhone companion: [iPhone installation](docs/IPHONE.md).
5. Select your LAN adapter, Fifine input and recording folder in Settings; save.
6. Permit the receiver only on the Windows **Private** network. The app offers a scoped firewall-rule button with a UAC prompt.
7. Connect the iPhone to the same LAN, open AirTake Camera and scan the desktop QR.
8. Click Record on Windows. After Stop, wait for upload and export to finish.

**An EXE alone cannot turn an iPhone into this camera. The companion must be installed and signed. No Apple credentials are included or requested by AirTake.**

## Implemented

- 3840×2160 or 1920×1080; 30/60/120 FPS; HEVC 20–200 Mbps.
- Exact camera-format/FPS validation. **No silent fallback, duplicated frames or upscaling to fake 120 FPS.**
- Hardware HEVC through VideoToolbox; compressed samples passed to a segmented MP4 writer.
- HTTPS/TLS with QR-pinned certificate and a random 256-bit bearer token.
- Approximately one-second MP4 fragments; SHA-256 and durable acknowledgements; retransmission/idempotence.
- Bounded persistent spool (128–4096 MiB) on the iPhone. Confirmed fragments are removed. Capture stops rather than overwriting untransmitted data.
- Windows WASAPI microphone selection (Fifine auto-selected by name when available), separate original WAV, automatic MP4/AAC export without re-encoding video.
- Approximate network-clock alignment plus an adjustable microphone offset.
- Actual capture/encoding counters, measured FPS, drop detection, thermal state, disk reserve, low-resolution preview, per-take manifests and export logs.
- Native camera focus/exposure/white-balance locks on iPhone.
- Repeated export and recovery of already completed fragments.

## Verification boundaries

This is a **preview**, not a certified lossless 4K/120 product. CI tests can verify compilation, transport, checksums, persistence and remuxing of a **synthetic 4K/120 file**. They cannot certify real iPhone capture, Wi-Fi throughput, USB audio latency, heat, exposure, long-take A/V drift or frame preservation across every iOS interruption. A successful synthetic test is not a successful real-camera test.

Test a 60-second take before relying on it, then a long take and a brief Wi-Fi interruption. Actual 4K/120 availability is checked against the device's AVFoundation formats. The receiver has no reason to need a powerful GPU: it records compressed data and remuxes rather than encoding the video again.

## Storage

At an **average** 120 Mbps the original video is approximately 54 GB/hour (decimal). This does not include WAV, metadata or export copies. The receiver retains chunks, assembled video and the final MP4: allow **up to three video copies**, plus audio. During a brief outage the phone temporarily uses its configured spool; that is deliberate, not a full duplicate recording.

The last unfinalized fragment may be lost if iOS kills the app or the phone loses power. Recovered crash sessions are labelled interrupted, never clean. If the network remains slower than capture, no finite buffer can preserve an unlimited take. Do not close the phone app while unacknowledged data remains.

## Build

- Windows: .NET 8 SDK, `./scripts/build-windows.ps1`; `tools/ffmpeg.exe` + `tools/ffprobe.exe` come from the media CI artifact.
- iPhone: Xcode with iOS 18 SDK or newer, XcodeGen, `cd ios && xcodegen generate`, then open the generated project and choose your signing team.
- Tests: `dotnet test tests/AirTake.Tests/AirTake.Tests.csproj`.
- GitHub Actions builds Windows, compiles an unsigned iPhone app, runs protocol/storage and Windows smoke/integration tests, then attaches distributables and FFmpeg source to a preview release.

See [protocol](docs/PROTOCOL.md), [hardware acceptance](docs/ACCEPTANCE.md) and [third-party software](docs/THIRD_PARTY.md).
