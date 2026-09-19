# AirTake · Moblin edition

Windows-only recorder for **Moblin → encrypted SRT over local Wi-Fi → computer disk**, with an existing **Fifine or another selected Windows microphone**.

**No custom iPhone application, Apple signing, Xcode, subscription, account, cloud relay, or OBS required.** Install the existing Moblin app from the App Store on the iPhone. This edition is independent of the native iOS/TLS implementation elsewhere in this repository.

## Start on Windows

1. Extract the complete AirTake-Moblin-Windows-x64.zip folder and run **AirTake.exe**.
2. Click **Подготовить движок** once. The app downloads the pinned FFmpeg Windows build over HTTPS and verifies its SHA-256 before execution. Internet is needed for this initial download only.
3. Open **Настройки и файлы**, select **Fifine**, choose the recording folder, save, and run the 3-second microphone test.
4. Open **Подключение Moblin**, select the computer's LAN IPv4 (not a VPN/virtual adapter), save, and click the private-network firewall button. Windows asks for elevation only for that explicit action.
5. On the iPhone, join the same network, open Moblin, import the QR profile, select the main rear **1×** camera and landscape orientation. Verify **3840×2160, 120 FPS, H.265/HEVC** in Moblin. Its microphone stays on as an alignment reference.
6. In AirTake click **Начать приём и запись**, then start **Live/Stream** in Moblin.
7. Stop in **AirTake first**. Keep Moblin streaming until AirTake has saved the source; then stop the phone. The final file contains the selected computer microphone, not the phone microphone.

The GUI is a local Edge app window, with default-browser fallback. It is served only on 127.0.0.1 with a random per-launch access token. No developer tools or language runtime are needed by the end user. Windows 10/11 x64 is the target; the browser engine must already be installed.

## Recording behavior

* Video uses `-c:v copy` / stream copy: no re-encoding, upscaling, frame duplication, or conversion of 60 FPS into 120 FPS.
* Moblin profiles are generated locally, including an encrypted SRT URL and the actual requested camera settings. Those settings change Moblin only after profile import; this is not a remote camera-control API.
* Audio is recorded separately through FFmpeg DirectShow as mono 48 kHz, 24-bit RF64-capable WAV. Device alternative IDs distinguish identical device names. A microphone failure stops video; the app never silently substitutes phone audio.
* Automatic alignment matches audio envelopes from Moblin and Fifine. For longer recordings it attempts an end-of-take drift estimate. Ambiguous or silent references cause an explicit error, preserving the originals. Manual offset and re-export are available. Positive offset moves sound later. Do not assume sample-accurate synchronization on untested rooms/devices.
* Every take has `source-0001.mkv`, `microphone.wav`, `take.json`, and `capture.log`. Successful exports add `final-0001-....mkv` or `.mp4`, retaining every original. Interrupted connections create new numbered parts; each part is exported separately, never pretending that a missing interval was recovered.
* MKV final audio is FLAC; MP4 final audio is AAC. The video remains HEVC/H.264 stream copy in either case.
* Disk reserve, maximum duration, reconnect, password, SRT latency, profile bitrate/FPS/resolution, manual audio offset, auto-export, and optional separate FFplay preview are all in the GUI. Camera focus/exposure/white balance remain in Moblin.

## Important limits

**Moblin labels 120 FPS experimental.** This application can preserve an incoming 4K/120 stream; it cannot make Moblin or an iPhone sustain that mode. Actual iPhone 16 Pro camera, thermal behavior, your Fifine, and your Wi-Fi have not been hardware-qualified here. A successful synthetic transport test is not a certification of the complete phone setup.

SRT retransmission is limited by its live latency window. Packets beyond that window, a crashed sender, and missing time during reconnection cannot be recovered by the receiver. The app has **no local iPhone safety spool**, no guaranteed zero-loss recording, and no visibility into dropped sensor frames before transmission. Keep local Moblin recording as a separate option when needed; AirTake does not enable it remotely.

**Stop the receiver before stopping the sender.** A synthetic test initially exposed lost tail packets when the sender closed with zero linger; the verified transport test explicitly gives its sender time to drain. Normal AirTake user-stop allows a bounded receiver-latency drain. Abruptly closing Moblin can still truncate the tail.

The UI frame count counts received/muxed video packets, not independently verified unique sensor frames. Live frame/time FPS is approximate (audio/container timing and startup buffering can affect it). Capture logs include the source format, and finalized files are probed again.

At a *video* bitrate of 100 Mbps the calculation is 45 GB/hour; 120 Mbps is 54 GB/hour. Audio, container overhead and retained final files add disk usage. Export needs space for another video copy; choose sufficient free space. Wi-Fi should have headroom over the selected bitrate. A localhost test does not qualify a wireless network.

## Dependencies, safety and privacy

AirTake source uses the Go standard library only. QR generation is local, with a small original encoder and attributed standard table data. It has no CDN scripts, analytics or telemetry.

FFmpeg is a **separate third-party executable**, not linked into AirTake. This download does **not redistribute FFmpeg**. The optional first-run action downloads the unmodified vendor archive directly:

* https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.1-essentials_build.zip
* SHA-256: `fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9`
* Build/license/source information: https://www.gyan.dev/ffmpeg/builds/

Alternatively place a trusted SRT-enabled `ffmpeg.exe`, `ffprobe.exe`, and optionally `ffplay.exe` in `tools/` beside AirTake.exe. Downloaded programs live in `%APPDATA%\AirTake\tools`; local settings live in `%APPDATA%\AirTake`. Do not share the pairing QR/profile: it contains the SRT password. Diagnostics redact that password; they may contain local paths/device names.

A firewall button creates only an inbound UDP rule for the selected FFmpeg executable, port, local IPv4, **Private** network profile and **LocalSubnet**. It does not disable the firewall, change the network profile, open an internet router port, or require administrator rights for normal recording. Windows microphone privacy permission must allow desktop apps. Distribution is unsigned; AirTake never changes Windows security policy or SmartScreen settings.

## Build and test

From this directory (Go 1.23 or newer):

```powershell
go test -race ./...
$env:GOOS='windows'; $env:GOARCH='amd64'; $env:CGO_ENABLED='0'
go build -trimpath -ldflags='-s -w -H=windowsgui' -o dist/AirTake.exe ./cmd/airtake
```

For media integration tests, use a trusted FFmpeg build with SRT/libx264/libx265 on PATH:

```powershell
$env:AIRTAKE_MEDIA_TESTS='1'
$env:AIRTAKE_SRT_TESTS='1'
go test -v ./internal/engine
```

Tests cover config and escaping, stream-copy/no-interpolation command construction, DirectShow device parsing, secret redaction, atomic settings, path validation, audio offset signs, correlation ambiguity/silence, QR golden matrices, real MKV/MP4 export, a deterministic 2.300 s microphone shift, and an encrypted **3840×2160 / HEVC / 120 FPS, 360→360 packet** localhost transfer. Media tests are opt-in because they require external binaries.

For development without opening a browser, set `AIRTAKE_NO_BROWSER=1`. `AIRTAKE_DATA_DIR` redirects the configuration directory for smoke tests. Read `instance.json` there and supply its token in the `X-AirTake-Token` header when testing the local API; never publish that token.

## Primary technical references

* Moblin source and 120-FPS warning: https://github.com/eerimoq/moblin
* Profile fields: https://github.com/eerimoq/moblin/blob/main/Moblin/Various/MoblinSettingsUrl.swift
* FFmpeg SRT options and latency units: https://ffmpeg.org/ffmpeg-protocols.html#srt
* Windows capture: https://ffmpeg.org/ffmpeg-devices.html#dshow

License: MIT for AirTake, with BSD-3-Clause notices for QR reference data. Third-party executables retain their own licenses.
