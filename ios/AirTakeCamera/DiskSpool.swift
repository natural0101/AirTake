import Foundation

struct SpoolMetadata: Codable {
    var id: String
    var options: CaptureOptions
    var nextIndex = 0
    var acknowledged = 0
    var closed = false
    var endSent = false
    var end = TakeEnd()
}
final class DiskSpool {
    private let lock = NSLock()
    private let root: URL
    private var meta: SpoolMetadata?
    private var bytes: Int64 = 0
    var onPressure: (() -> Void)?
    init(directory: URL? = nil) throws {
        if let directory { root = directory }
        else {
            let support = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            root = support.appendingPathComponent("AirTakeSpool", isDirectory: true)
        }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        var flags = URLResourceValues(); flags.isExcludedFromBackup = true
        var mutableRoot = root; try mutableRoot.setResourceValues(flags)
        if FileManager.default.fileExists(atPath: metadataURL.path) {
            meta = try JSONDecoder().decode(SpoolMetadata.self, from: Data(contentsOf: metadataURL))
            guard let state = meta, UUID(uuidString: state.id) != nil, state.nextIndex >= 0, state.acknowledged >= 0, state.acknowledged <= state.nextIndex else { throw AirTakeError.message("Журнал очереди повреждён. Фрагменты оставлены на диске.") }
            for url in try segmentFiles() {
                guard let index = Int(url.deletingPathExtension().lastPathComponent) else { continue }
                if index < meta!.acknowledged { try FileManager.default.removeItem(at: url); continue }
                bytes += try size(url); meta!.nextIndex = max(meta!.nextIndex, index + 1)
            }
            if meta!.closed == false {
                meta!.closed = true; meta!.end.interrupted = true
                meta!.end.error = "Приложение iPhone было закрыто. Последний незавершённый фрагмент мог не сохраниться."
            }
            try saveLocked()
        }
    }
    private var metadataURL: URL { root.appendingPathComponent("spool.json") }
    private func segmentFiles() throws -> [URL] { try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.fileSizeKey]).filter { $0.pathExtension == "seg" }.sorted { $0.lastPathComponent < $1.lastPathComponent } }
    private func segmentURL(_ index: Int) -> URL { root.appendingPathComponent(String(format: "%08d.seg", index)) }
    private func size(_ url: URL) throws -> Int64 { Int64(try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0) }
    private func durableWrite(_ data: Data, to url: URL) throws {
        try data.write(to: url, options: .atomic)
        let handle = try FileHandle(forWritingTo: url)
        try handle.synchronize(); try handle.close()
    }
    private func saveLocked() throws {
        guard let meta else { return }; try durableWrite(JSONEncoder().encode(meta), to: metadataURL)
    }
    var metadata: SpoolMetadata? { lock.lock(); defer { lock.unlock() }; return meta }
    var pendingBytes: Int64 { lock.lock(); defer { lock.unlock() }; return bytes }
    func create(id: String, options: CaptureOptions) throws {
        lock.lock(); defer { lock.unlock() }
        guard meta == nil else { throw AirTakeError.message("Сначала завершите передачу предыдущей записи.") }
        guard UUID(uuidString: id) != nil else { throw AirTakeError.message("Некорректный идентификатор дубля.") }
        meta = SpoolMetadata(id: id, options: options); bytes = 0; try saveLocked()
    }
    func append(_ data: Data) throws {
        var pressure = false
        lock.lock()
        do {
            guard var state = meta, !state.closed else { throw AirTakeError.message("Нет открытой записи для фрагмента.") }
            guard data.count > 0, data.count <= 64 * 1024 * 1024 else { throw AirTakeError.message("Размер фрагмента должен быть от 1 байта до 64 МиБ.") }
            let free = try root.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage ?? 0
            guard free > Int64(data.count) + 128 * 1024 * 1024 else { throw AirTakeError.message("Недостаточно памяти iPhone. Сохранённые фрагменты не удалены.") }
            let limit = Int64(state.options.bufferMiB) * 1024 * 1024
            guard bytes + Int64(data.count) <= limit + 128 * 1024 * 1024 else { throw AirTakeError.message("Переполнен аварийный буфер. Запись прервана; очередь сохранена.") }
            try durableWrite(data, to: segmentURL(state.nextIndex))
            state.nextIndex += 1; bytes += Int64(data.count); meta = state; try saveLocked()
            pressure = bytes >= limit; lock.unlock()
        } catch { lock.unlock(); throw error }
        if pressure { onPressure?() }
    }
    func nextPending() -> (id: String, index: Int, url: URL)? {
        lock.lock(); defer { lock.unlock() }
        guard let state = meta, state.acknowledged < state.nextIndex else { return nil }
        return (state.id, state.acknowledged, segmentURL(state.acknowledged))
    }
    func acknowledge(_ next: Int) throws {
        lock.lock(); defer { lock.unlock() }
        guard var state = meta, next >= state.acknowledged, next <= state.nextIndex else { throw AirTakeError.message("Некорректное подтверждение от ПК.") }
        let previous = state.acknowledged; state.acknowledged = next; meta = state
        try saveLocked()
        for index in previous..<next {
            let url = segmentURL(index)
            if FileManager.default.fileExists(atPath: url.path) { bytes -= try size(url); try FileManager.default.removeItem(at: url) }
        }
    }
    func updateMetrics(_ end: TakeEnd) throws {
        lock.lock(); defer { lock.unlock() }
        guard meta != nil, meta!.closed == false else { return }
        meta!.end = end; try saveLocked()
    }
    func close(_ end: TakeEnd) throws {
        lock.lock(); defer { lock.unlock() }
        guard meta != nil else { return }; meta!.end = end; meta!.closed = true; try saveLocked()
    }
    func markEndSent() throws { lock.lock(); defer { lock.unlock() }; meta?.endSent = true; try saveLocked() }
    func clearCompleted() throws {
        lock.lock(); defer { lock.unlock() }
        guard let state = meta, state.closed, state.acknowledged == state.nextIndex else { throw AirTakeError.message("Нельзя удалить неподтверждённую запись.") }
        if FileManager.default.fileExists(atPath: metadataURL.path) { try FileManager.default.removeItem(at: metadataURL) }
        for url in try segmentFiles() { try FileManager.default.removeItem(at: url) }
        meta = nil; bytes = 0
    }
}
