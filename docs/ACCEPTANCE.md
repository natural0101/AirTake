# Physical-device acceptance (not yet executed by CI)

Record results alongside the exact app commit, iOS version, router/network adapter and Fifine device/driver. Do not label the release production-validated until these checks have actual evidence.

1. Pair an iPhone 16 Pro main rear camera with Windows and the selected Fifine. Confirm the certificate pin/QR and reject an incorrect token.
2. Select 3840x2160 / 120, verify supported format and hardware HEVC flag. Record a moving subject with a visible timecode; verify dimensions, unique captured frames, PTS progression, playback duration and counters. Merely seeing 120 in a file header is insufficient.
3. Record for 1, 10 and 30 minutes. Log measured captured FPS, encoded count, capture/encoder/writer drops, thermal state, battery and disk bandwidth.
4. Compare files to frame counts in take.json. Measure the real bitrate rather than treating the target as a size guarantee.
5. Interrupt Wi-Fi for 5 and 15 seconds. Confirm the queue grows, catches up and produces a continuous file without unreported holes.
6. Interrupt Wi-Fi beyond the configured queue capacity. Confirm recording stops, unacknowledged segments are not overwritten, the cause is explicit, and rejoining drains the queue.
7. Terminate the phone app and restart it. Confirm completed spool segments recover; mark the final in-progress segment as potentially lost, never claim seamless recovery.
8. Terminate the receiver and reopen the same output folder and identity. Confirm uploads resume and microphone interruption is explicitly reported.
9. Disconnect the microphone during capture. Confirm the take is stopped with an error, not silently replaced by phone audio.
10. Test a nearly-full disk, corrupt a copied source segment and retransmit a changed index. Verify no premature success/ACK, original evidence retained, checksum conflict rejected.
11. Clap at start and end of a 30-minute take. Measure audio offset/drift and apply the exposed correction. Long-take adaptive drift correction is not implemented.
12. Test backgrounding, screen lock, incoming camera interruption and critical thermal pressure. Confirm the stop reason is visible.
13. Check the GUI at 100%, 150% and 200% DPI; folder paths with spaces/Cyrillic; microphone names with Unicode; manually selected VPN/physical network interfaces.

CI tests only establish compile success, the exercised protocol/disk behaviors, a small HEVC/120fps fixture's muxing and a real EXE startup. They do not replace any hardware test above.
