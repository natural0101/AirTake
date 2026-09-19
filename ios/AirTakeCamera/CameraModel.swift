import SwiftUI
import AVFoundation
import UIKit

@MainActor
final class CameraModel: ObservableObject {
    let engine = CameraEngine()
    @Published var connection = "Подключите iPhone к AirTake на ПК"
    @Published var error: String?
    @Published var stats = CameraStats()
    @Published var bufferedBytes: Int64 = 0
    @Published var recording = false
    @Published var scanning = false
    @Published var manualPairing = ""
    @Published var focusLocked = false, exposureLocked = false, whiteBalanceLocked = false
    private var api: ReceiverAPI?
    private var pollTask: Task<Void, Never>?, uploadTask: Task<Void, Never>?
    private var sessions: [SpoolSession] = []
    private var handledTake: UUID?
    private var activeSpool: SpoolSession?
    private var previewJpeg: String?
    private var activated = false

    init() {
        engine.onError = { [weak self] message in Task { @MainActor in self?.error = String(message.prefix(1800)) } }
        engine.onPreview = { [weak self] image in Task { @MainActor in self?.previewJpeg = image } }
        engine.onFinished = { [weak self] in Task { @MainActor in
            guard let self else { return }; self.recording = false
            if let spool = self.activeSpool, (try? spool.removeIfEmpty()) == true {
                self.sessions.removeAll { $0.id == spool.id }; self.activeSpool = nil
            }
        } }
    }
    func activate() async {
        guard !activated else { return }; activated = true
        UIApplication.shared.isIdleTimerDisabled = true
        let permitted = await AVCaptureDevice.requestAccess(for: .video)
        if !permitted { error = "Разрешите доступ к камере в настройках iOS" }
        sessions = SpoolSession.restore()
        for session in sessions { do { try session.recoverAfterCrash() } catch { self.error = error.localizedDescription } }
        if let pairing = PairingKeychain.load() { await connect(pairing) }
    }
    func connect(text: String) async {
        do {
            let pairing = try JSONDecoder().decode(Pairing.self, from: Data(text.utf8))
            await connect(pairing)
        } catch { self.error = "Не удалось прочитать подключение: " + error.localizedDescription }
    }
    private func connect(_ pairing: Pairing) async {
        guard !engine.isRecording else { error = "Сначала остановите запись"; return }
        pollTask?.cancel(); uploadTask?.cancel(); api?.close()
        do {
            let api = try ReceiverAPI(pairing: pairing); self.api = api
            _ = try await api.time(); try PairingKeychain.save(pairing)
            connection = "Подключено: " + (URL(string: pairing.url)?.host ?? "ПК"); error = nil
            pollTask = Task { [weak self] in await self?.poll(api) }
            uploadTask = Task { [weak self] in await self?.upload(api) }
        } catch { self.error = error.localizedDescription; connection = "Подключение не удалось. Проверьте Wi-Fi и брандмауэр ПК." }
    }
    private func poll(_ api: ReceiverAPI) async {
        while !Task.isCancelled {
            stats = engine.snapshot; recording = engine.isRecording
            bufferedBytes = sessions.reduce(0) { $0 + $1.queueBytes }
            let status = PhoneStatus(name: UIDevice.current.name, fps: stats.fps, captured: stats.captured, encoded: stats.encoded,
                dropped: stats.dropped, queueBytes: bufferedBytes, thermal: thermalName,
                error: error, takeId: activeSpool?.id, previewJpeg: previewJpeg)
            do {
                let control = try await api.status(status)
                connection = "Подключено к ПК · TLS"
                if control.recording, let id = control.takeId, id != handledTake {
                    guard !engine.isRecording, sessions.isEmpty else {
                        error = "Сначала передайте предыдущий дубль из буфера"; handledTake = id
                        continue
                    }
                    error = nil
                    let clockOffset = try await api.synchronizeClock()
                    let spool = try SpoolSession(id: id, settings: control.settings)
                    handledTake = id; activeSpool = spool; sessions.append(spool)
                    engine.start(settings: control.settings, spool: spool, clockOffset: clockOffset)
                } else if !control.recording && engine.isRecording {
                    engine.stop()
                }
            } catch {
                // Network failure never cancels an otherwise healthy capture; the bounded disk spool absorbs it.
                connection = "Связь с ПК прервана. Повторное подключение…"
            }
            try? await Task.sleep(nanoseconds: 500_000_000)
        }
    }
    private func upload(_ api: ReceiverAPI) async {
        var backoff: UInt64 = 250_000_000
        while !Task.isCancelled {
            var progressed = false
            do {
                for spool in sessions {
                    if Task.isCancelled { return }
                    if let chunk = try await Task.detached(priority: .utility, operation: { try spool.next() }).value {
                        try await api.upload(id: spool.id, sequence: chunk.0, file: chunk.1, hash: chunk.2)
                        try spool.acknowledge(chunk.1); progressed = true
                    } else if let completion = spool.completion {
                        connection = "Фрагменты на ПК. Ожидание экспорта…"
                        try await api.complete(id: spool.id, completion: completion)
                        try spool.removeCompleted(); sessions.removeAll { $0.id == spool.id }
                        if activeSpool?.id == spool.id { activeSpool = nil }
                        connection = "Дубль сохранён на ПК"; progressed = true
                    }
                }
                backoff = 250_000_000
            } catch {
                connection = "Передача приостановлена; буфер сохранён. " + String(error.localizedDescription.prefix(180))
                try? await Task.sleep(nanoseconds: backoff); backoff = min(backoff * 2, 8_000_000_000)
            }
            if !progressed { try? await Task.sleep(nanoseconds: 150_000_000) }
        }
    }
    var thermalName: String {
        switch ProcessInfo.processInfo.thermalState {
        case .nominal: "норма"
        case .fair: "тёплый"
        case .serious: "горячий"
        case .critical: "критический"
        @unknown default: "неизвестно"
        }
    }
    func updateLocks() { engine.setLocks(focus: focusLocked, exposure: exposureLocked, whiteBalance: whiteBalanceLocked) }
    func stop() { engine.stop() }
    func prepareScanner() {
        guard !engine.isRecording else { return }
        engine.pausePreview(); scanning = true
    }
    func backgrounded() { if engine.isRecording { engine.stop(reason: "Приложение переведено в фон") } }
}
