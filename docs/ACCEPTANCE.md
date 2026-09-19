# Hardware acceptance — NOT yet performed

Do not turn this checklist into PASS without actual hardware evidence.

1. Windows 10/11 x64: cold launch ZIP, no .NET installed, choose output volume, save settings, restart.
2. iPhone 16 Pro: install signed IPA, permit local network/camera, pair over LAN; no phone microphone permission requested.
3. 4K/120/HEVC 120 Mbps, main 1× camera: 60 s take; inspect dimensions, encoded frame count, PTS monotonicity and measured frame cadence with ffprobe.
4. Fifine: select the actual device; verify source WAV, final AAC, audible voice, clap alignment and offset sign.
5. 20 min take: check drops, heat, duration and start/end A/V drift; no silent fallback or synthetic frame duplication.
6. Disable Wi-Fi for 10 s; restore while under buffer limit; verify all chunks, final frame count and hash receipts.
7. Keep Wi-Fi off until buffer limit; verify capture stops and pending chunks are not overwritten.
8. Kill/relaunch the phone app; verify pending completed fragments resume and the take is marked interrupted.
9. Restart the Windows receiver during an outage; verify existing manifest/chunks load and duplicate delivery is harmless.
10. Fill PC disk to reserve; verify visible error, no false completion and no discarded phone buffer.
11. Unplug Fifine; verify visible error and preserved video/audio originals.
12. Wrong QR token/certificate: refuse connection; do not open firewall globally.

Automated tests cover software invariants only. The CI media fixture is generated video, not a recording from an iPhone.
