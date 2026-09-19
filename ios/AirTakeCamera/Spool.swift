import Foundation

struct SpoolState: Codable {
    var id: UUID, settings: CaptureSettings, nextSequence = 0
    var captured: Int64 = 0, encoded: Int64 = 0, dropped: Int64 = 0
    var firstVideoTimeMs = 0.0, durationSeconds = 0.0
    var completion: Completion?
}
final class SpoolSession: @unchecked Sendable {
    let folder: URL
    private let lock = NSLock()
    private var state: SpoolState
    var id: UUID { lock.guarded { state.id } }
    static var root: URL {
        let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Spool", isDirectory: true)
        return root
    }
    init(id: UUID, settings: CaptureSettings) throws {
        folder = Self.root.appendingPathComponent(id.uuidString, isDirectory: true)
        guard !FileManager.default.fileExists(atPath: folder.path) else { throw AirError.message("Этот дубль уже существует в буфере") }
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var resource = URLResourceValues(); resource.isExcludedFromBackup = true
        var url = folder; try url.setResourceValues(resource)
        state = SpoolState(id: id, settings: settings); try save()
    }
    init(folder: URL) throws {
        self.folder = folder; state = try JSONDecoder().decode(SpoolState.self, from: Data(contentsOf: folder.appendingPathComponent("state.json")))
    }
    private func save() throws { try JSONEncoder().encode(state).write(to: folder.appendingPathComponent("state.json"), options: .atomic) }
    var queueBytes: Int64 { lock.guarded { (try? chunkFiles().reduce(Int64(0)) { $0 + Int64((try $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }) ?? 0 } }
    private func chunkFiles() throws -> [URL] {
        try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.fileSizeKey]).filter { $0.pathExtension == "chunk" }.sorted { $0.lastPathComponent < $1.lastPathComponent }
    }
    func append(_ data: Data, stats: CameraStats) throws -> Bool {
        try lock.guarded {
            guard data.count <= 64 * 1024 * 1024 else { throw AirError.message("Фрагмент превышает 64 МиБ") }
            let available = try folder.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage ?? 0
            guard available > Int64(data.count) + 256 * 1024 * 1024 else { throw AirError.message("Заканчивается свободное место iPhone") }
            let name = String(format: "%08d", state.nextSequence)
            let file = folder.appendingPathComponent(name + ".chunk")
            try data.write(to: file, options: .atomic)
            state.nextSequence += 1; state.captured = stats.captured; state.encoded = stats.encoded; state.dropped = stats.dropped
            state.firstVideoTimeMs = stats.firstVideoTimeMs; state.durationSeconds = stats.durationSeconds
            try save()
            let count = try chunkFiles().reduce(Int64(0)) { $0 + Int64((try $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
            // Keep the fragment that crossed the threshold; stop capture instead of deleting data.
            return count <= Int64(state.settings.bufferMiB) * 1024 * 1024
        }
    }
    func finish(stats: CameraStats, reason: String) throws {
        try lock.guarded {
            guard state.nextSequence >= 2, stats.encoded > 0 else { throw AirError.message("Дубль не содержит завершённых видеофрагментов") }
            state.completion = Completion(chunkCount: state.nextSequence, captured: stats.captured, encoded: stats.encoded,
                dropped: stats.dropped, firstVideoTimeMs: stats.firstVideoTimeMs, durationSeconds: stats.durationSeconds, reason: reason)
            try save()
        }
    }
    // Called only at app launch, before a new capture can start.
    func recoverAfterCrash() throws {
        try lock.guarded {
            let sequences = try chunkFiles().compactMap { Int($0.deletingPathExtension().lastPathComponent) }
            if let maximum = sequences.max() { state.nextSequence = max(state.nextSequence, maximum + 1) }
            if state.completion == nil && state.nextSequence >= 2 && state.encoded > 0 {
                state.completion = Completion(chunkCount: state.nextSequence, captured: state.captured, encoded: state.encoded,
                    dropped: max(1, state.dropped), firstVideoTimeMs: state.firstVideoTimeMs,
                    durationSeconds: max(1.0 / Double(state.settings.fps), state.durationSeconds), reason: "app-interrupted")
                try save()
            }
        }
    }
    func next() throws -> (Int, URL, String)? {
        try lock.guarded {
            guard let file = try chunkFiles().first, let sequence = Int(file.deletingPathExtension().lastPathComponent) else { return nil }
            return (sequence, file, sha256(try Data(contentsOf: file, options: .mappedIfSafe)))
        }
    }
    func acknowledge(_ file: URL) throws { try lock.guarded { try FileManager.default.removeItem(at: file) } }
    var completion: Completion? { lock.guarded { state.completion } }
    func removeIfEmpty() throws -> Bool {
        try lock.guarded {
            guard state.nextSequence == 0, state.encoded == 0, try chunkFiles().isEmpty else { return false }
            try FileManager.default.removeItem(at: folder); return true
        }
    }
    func removeCompleted() throws { try lock.guarded { try FileManager.default.removeItem(at: folder) } }
    static func restore() -> [SpoolSession] {
        guard let dirs = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) else { return [] }
        return dirs.compactMap { try? SpoolSession(folder: $0) }
    }
}
