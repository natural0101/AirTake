import AVFoundation
import SwiftUI
import UIKit

@MainActor
final class CaptureController: ObservableObject {
    let camera = CameraEngine()
    @Published var connected = false
    @Published var recording = false
    @Published var working = false
    @Published var scanning = false
    @Published var status = "Подключите Windows-приложение"
    @Published var errorText = ""
    @Published var receiverName = "Нет подключения"
    @Published var measuredFps = 0.0
    @Published var dropped: Int64 = 0
    @Published var pendingBytes: Int64 = 0
    @Published var mode = "4K / 120 — запрос"
    @Published var hardware = false
    @Published var speed = ""
    @Published var thermal = "unknown"
    private var spool: DiskSpool?
    private var api: ReceiverAPI?
    private var polling: Task<Void, Never>?
    private var uploading: Task<Void, Never>?
    private var configured: CaptureOptions?
    private var stopping = false
    private let clientId: String
    var hasPendingTake: Bool { spool?.metadata != nil }

    init() {
        let key = "AirTakeClientID"
        clientId = UserDefaults.standard.string(forKey: key) ?? UUID().uuidString.lowercased()
        UserDefaults.standard.set(clientId, forKey: key)
        do { spool = try DiskSpool() }
        catch { errorText = "Буфер не открылся: \(error.localizedDescription). Не удаляйте приложение: там могут быть фрагменты записи." }
        camera.onFailure = { [weak self] text in Task { @MainActor in await self?.stop(reason: text) } }
        spool?.onPressure = { [weak self] in Task { @MainActor in await self?.stop(reason: "Буфер достиг лимита. Запись остановлена; ожидается восстановление сети.") } }
    }
    func bootstrap() async {
        UIDevice.current.isBatteryMonitoringEnabled = true
        if let previous = PairingKeychain.read() { await connect(previous) }
    }
    func connect(_ code: String) async {
        guard !recording, !working, !scanning else { return }
        guard spool != nil else { return }
        working = true
        defer { working = false }
        do {
            let pairing = try Pairing(code)
            polling?.cancel(); uploading?.cancel(); api?.close()
            if let polling { await polling.value }; if let uploading { await uploading.value }
            polling = nil; uploading = nil; configured = nil
            let client = ReceiverAPI(pairing: pairing)
            api = client
            _ = try await client.syncClock()
            try PairingKeychain.write(pairing.original)
            receiverName = "\(pairing.host):\(pairing.port)"
            connected = true; errorText = ""; status = "Сопряжение подтверждено"
            polling = Task { [weak self] in await self?.poll() }
            startUploader()
        } catch { connected = false; errorText = error.localizedDescription; status = "Подключение не удалось" }
    }
    private func poll() async {
        while !Task.isCancelled {
            if scanning { try? await Task.sleep(nanoseconds: 250_000_000); continue }
            do {
                guard let api else { return }
                let control = try await api.get("/v1/control", as: ControlReply.self)
                guard control.version == 1 else { throw AirTakeError.message("Версии приложений несовместимы.") }
                connected = true
                if !recording, !working, !scanning, spool?.metadata == nil, configured != control.capture {
                    try await camera.configure(control.capture)
                    configured = control.capture
                    errorText = ""; status = "Готово к записи"
                }
                if control.recording, !recording, !working, !stopping, !scanning, spool?.metadata == nil { await begin(control.capture) }
                if !control.recording, recording { await stop() }
                let snapshot = camera.snapshot
                measuredFps = snapshot.fps; hardware = snapshot.hardware
                dropped = snapshot.end.captureDrops + snapshot.end.encoderDrops + snapshot.end.writerDrops
                pendingBytes = spool?.pendingBytes ?? 0
                if !snapshot.mode.isEmpty { mode = snapshot.mode }
                thermal = Self.thermalName()
                if recording { try spool?.updateMetrics(snapshot.end) }
                var telemetry = PhoneTelemetry()
                telemetry.clientId = clientId
                telemetry.status = !errorText.isEmpty ? "error" : recording ? "recording" : hasPendingTake ? "uploading" : "ready"
                telemetry.message = errorText
                telemetry.mode = mode; telemetry.fps = measuredFps; telemetry.captured = snapshot.end.captured
                telemetry.dropped = dropped; telemetry.pendingBytes = pendingBytes
                telemetry.hardwareEncoder = hardware; telemetry.battery = Double(UIDevice.current.batteryLevel); telemetry.thermal = thermal
                telemetry.formats = CameraEngine.formats()
                try await api.post("/v1/heartbeat", telemetry)
                if let preview = camera.takePreview() { try await api.sendPreview(preview) }
                if recording, ProcessInfo.processInfo.thermalState == .critical { await stop(reason: "Критический нагрев iPhone. Запись остановлена, FPS не подменялся.") }
                if recording, snapshot.end.duration >= 4 * 3600 { await stop(reason: "Достигнут лимит одного дубля: 4 часа.") }
            } catch {
                if Task.isCancelled { return }
                connected = false
                status = "Нет связи с ПК · \(error.localizedDescription)"
                if case AirTakeError.message = error {
                    errorText = error.localizedDescription
                    _ = try? await api?.post("/v1/heartbeat", PhoneTelemetry(clientId: clientId, status: "error", message: errorText))
                }
            }
            try? await Task.sleep(nanoseconds: 500_000_000)
        }
    }
    func requestRecording() async {
        if recording { await stop(); return }
        guard connected, !working, !scanning, !hasPendingTake, let api else { return }
        errorText = ""
        do { try await api.post("/v1/intent", Intent(recording: true)); status = "Подготовка записи…" }
        catch { errorText = error.localizedDescription }
    }
    private func begin(_ options: CaptureOptions) async {
        guard let spool, let api else { return }
        working = true
        defer { working = false }
        do {
            status = "Проверка формата и HEVC…"
            try await camera.configure(options)
            configured = options
            try await camera.arm()
            let clock = try await api.syncClock()
            let id = UUID().uuidString.lowercased()
            try spool.create(id: id, options: options)
            var startError: Error?
            for attempt in 0..<3 {
                do { try await api.post("/v1/start", Start(id: id, clientId: clientId)); startError = nil; break }
                catch { startError = error; if attempt < 2 { try? await Task.sleep(nanoseconds: 500_000_000) } }
            }
            if let startError { throw startError }
            recording = true
            UIApplication.shared.isIdleTimerDisabled = true
            await camera.begin(offset: clock.offset, rtt: clock.rtt) { data in try spool.append(data) }
            status = "REC · оригинальные кадры HEVC"; errorText = ""; startUploader()
        } catch {
            recording = false
            let end = await camera.finish(error: error.localizedDescription)
            try? spool.close(end)
            _ = try? await api.post("/v1/intent", Intent(recording: false))
            errorText = error.localizedDescription
            UIApplication.shared.isIdleTimerDisabled = false
            startUploader()
        }
    }
    func stop(reason: String = "") async {
        guard recording, !stopping, let spool else { if !reason.isEmpty { errorText = reason }; return }
        stopping = true; working = true; recording = false
        defer { stopping = false; working = false }
        status = "Завершение фрагментов…"
        let end = await camera.finish(error: reason)
        do { try spool.close(end) }
        catch { errorText = "Не удалось сохранить журнал: \(error.localizedDescription)" }
        if !reason.isEmpty { errorText = reason }
        UIApplication.shared.isIdleTimerDisabled = false
        configured = nil
        _ = try? await api?.post("/v1/intent", Intent(recording: false))
        status = "Передача оставшихся фрагментов…"; startUploader()
    }
    private func startUploader() {
        guard uploading == nil, api != nil, spool != nil else { return }
        uploading = Task { [weak self] in await self?.uploadLoop() }
    }
    func retryUpload() { errorText = ""; startUploader() }
    private func uploadLoop() async {
        defer { uploading = nil }
        var attempt = 0
        while !Task.isCancelled {
            guard let spool, let api else { return }
            guard let state = spool.metadata else { try? await Task.sleep(nanoseconds: 250_000_000); continue }
            do {
                if state.closed, state.nextIndex == 0 {
                    do { try await api.post("/v1/takes/\(state.id)/abort", Empty()) }
                    catch AirTakeError.http(let code, _) where code == 404 { }
                    try spool.clearCompleted(); status = "Пустой дубль отменён"; attempt = 0; continue
                }
                if state.closed, !state.endSent { try await api.post("/v1/takes/\(state.id)/end", state.end); try spool.markEndSent() }
                if let pending = spool.nextPending() {
                    let acknowledged = try await api.upload(id: pending.id, index: pending.index, url: pending.url)
                    try spool.acknowledge(acknowledged); pendingBytes = spool.pendingBytes; attempt = 0
                } else if state.closed {
                    status = "ПК собирает MP4…"
                    try await api.post("/v1/takes/\(state.id)/finish", Finish(chunks: state.nextIndex))
                    try spool.clearCompleted()
                    status = state.end.interrupted ? "Сохранено с предупреждением: проверьте take.json" : "MP4 сохранён на ПК"
                    pendingBytes = 0; attempt = 0
                } else { try? await Task.sleep(nanoseconds: 100_000_000) }
            } catch {
                if Task.isCancelled { return }
                if case AirTakeError.http(let code, _) = error, (400...499).contains(code), code != 408, code != 429 {
                    await stop(reason: error.localizedDescription)
                    errorText = error.localizedDescription + ". Очередь сохранена. Исправьте причину и нажмите «Повторить передачу»."
                    return
                }
                attempt = min(attempt + 1, 5)
                status = "Повтор передачи · очередь сохранена на iPhone"
                try? await Task.sleep(nanoseconds: UInt64(min(8.0, pow(2.0, Double(attempt - 1)) * 0.5) * 1_000_000_000))
            }
        }
    }
    func testNetwork() async {
        guard let api, !recording, !working, !hasPendingTake else { return }
        working = true
        defer { working = false }
        do { let result = try await api.speedTest(); speed = String(format: "%.0f Мбит/с · тест 32 МиБ", result) }
        catch { errorText = error.localizedDescription }
    }
    func prepareScanner() async {
        guard !working, !recording else { return }
        scanning = true
        await camera.pausePreview(); configured = nil
    }
    func finishScanner() { scanning = false; configured = nil }
    func backgrounded() async {
        guard recording else { return }
        let task = UIApplication.shared.beginBackgroundTask(withName: "AirTake finish")
        await stop(reason: "Приложение ушло с экрана. Запись остановлена; завершённые фрагменты сохранены.")
        if task != .invalid { UIApplication.shared.endBackgroundTask(task) }
    }
    private static func thermalName() -> String {
        switch ProcessInfo.processInfo.thermalState {
        case .nominal: return "нормальный"
        case .fair: return "тёплый"
        case .serious: return "высокий"
        case .critical: return "критический"
        @unknown default: return "неизвестно"
        }
    }
    private struct Intent: Encodable { let recording: Bool }
    private struct Start: Encodable { let id: String; let clientId: String }
    private struct Finish: Encodable { let chunks: Int }
    private struct Empty: Encodable { }
}
