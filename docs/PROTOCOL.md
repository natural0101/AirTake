# AirTake transport v1

HTTPS/TCP is used deliberately: reliable, ordered delivery, standard platform TLS and a small failure surface. QUIC and a live webcam transport are not required for disk-first recording. This is a buffered recorder, not a guaranteed sub-frame-latency stream.

Pairing JSON: `{ "version":1, "url":"https://192.168.1.10:49712", "token":"64 hex characters", "fingerprint":"SHA-256 of server DER certificate" }`.

All endpoints require `Authorization: Bearer <token>`. The iOS client checks the exact certificate fingerprint and original host, rejects redirects, and permits only private/loopback IPv4 receiver addresses. It does not disable system certificate validation globally. The Windows firewall helper permits a single TCP port/program for Private/LocalSubnet only.

| Method | Endpoint | Function |
|---|---|---|
| GET | /api/time | Monotonic-anchored server epoch milliseconds |
| POST | /api/status | Camera telemetry; returns current recording command/settings |
| PUT | /api/takes/{UUID}/chunks/{integer} | Raw fragment + Content-Length + X-Content-SHA256 |
| POST | /api/takes/{UUID}/complete | Seal expected sequence count, counters and capture timing |
| GET | /api/takes/{UUID} | Persisted take manifest |

Sequence 0 is the MP4 initialization segment; 1..N are subsequent segments. Max chunk: 64 MiB. ACK follows hash/length validation, Flush(true), durable receipt and atomic rename. Same sequence/hash is idempotent; a conflicting hash is rejected. Session paths are UUID-derived. Completion verifies all sequences and rechecks disk hashes before assembling MP4. No output is declared complete with a missing fragment.

The iPhone keeps a persistent bounded spool. Acknowledged chunks are removed only after a matching receipt. Disconnections back off and retry. A full buffer stops capture without evicting pending chunks. Terminating the app can lose the currently unfinalized fragment, but does not intentionally remove completed pending fragments. Crash-recovered takes are visibly marked interrupted.

Seven short clock probes choose the lowest-round-trip offset. Original capture PTS is translated to receiver time. Windows estimates the first WASAPI sample timestamp; this is not sample-accurate clock synchronization. Manual offset is applied during export. A clap test is required; long-term device-clock drift has not been hardware-qualified.

MP4 export invokes a separate bundled LGPL FFmpeg process, copies HEVC video and encodes microphone audio to AAC/48 kHz. Originals and failures are retained. No upload to any internet service occurs during recording.
