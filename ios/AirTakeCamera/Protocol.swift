import Foundation
import CryptoKit
import Security
import CoreMedia

struct CaptureSettings: Codable {
    var width = 3840, height = 2160, fps = 120, bitrateMbps = 120, bufferMiB = 1024
    var stopOnDroppedFrame = true
    func validate() throws {
        guard [(3840, 2160), (1920, 1080)].contains(where: { $0.0 == width && $0.1 == height }),
              [30, 60, 120].contains(fps), (20...200).contains(bitrateMbps), (128...4096).contains(bufferMiB)
        else { throw AirError.message("Некорректные настройки приёмника") }
    }
}
struct Pairing: Codable {
    var version: Int
    var url, token, fingerprint: String
    func validate() throws {
        guard version == 1, let u = URL(string: url), u.scheme == "https", let host = u.host,
              u.user == nil, u.password == nil, u.query == nil, u.fragment == nil,
              u.path.isEmpty || u.path == "/", token.count == 64, fingerprint.count == 64,
              token.allSatisfy({ $0.isHexDigit }), fingerprint.allSatisfy({ $0.isHexDigit })
        else { throw AirError.message("Некорректный QR AirTake") }
        let numbers = host.split(separator: ".").compactMap { Int($0) }
        guard numbers.count == 4, numbers.allSatisfy({ (0...255).contains($0) }),
              numbers[0] == 10 || (numbers[0] == 192 && numbers[1] == 168) ||
              (numbers[0] == 172 && (16...31).contains(numbers[1])) || numbers[0] == 127
        else { throw AirError.message("Разрешён только адрес ПК в локальной IPv4-сети") }
    }
}
struct Control: Decodable { let takeId: UUID?; let recording: Bool; let settings: CaptureSettings }
struct PhoneStatus: Encodable {
    var name: String; var fps: Double; var captured, encoded, dropped, queueBytes: Int64
    var thermal: String; var error: String?; var takeId: UUID?; var previewJpeg: String?
}
struct Completion: Codable {
    var chunkCount: Int; var captured, encoded, dropped: Int64
    var firstVideoTimeMs, durationSeconds: Double; var reason: String
}
struct Receipt: Codable { let sequence: Int; let bytes: Int64; let sha256: String }
struct TimeReply: Decodable { let serverTimeMs: Double }
struct Success: Decodable { let ok: Bool }
enum AirError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case .message(let message) = self { return message }; return nil }
}
func hostTimeMs() -> Double { CMClockGetTime(CMClockGetHostTimeClock()).seconds * 1000 }
func sha256(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
extension NSLock {
    func guarded<T>(_ work: () throws -> T) rethrows -> T { lock(); defer { unlock() }; return try work() }
}
// Pairing secrets remain in the device-only Keychain, not in preferences or logs.
enum PairingKeychain {
    private static var query: [String: Any] { [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "AirTake.Pairing", kSecAttrAccount as String: "receiver"] }
    static func save(_ pairing: Pairing) throws {
        let data = try JSONEncoder().encode(pairing)
        SecItemDelete(query as CFDictionary)
        var item = query; item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else { throw AirError.message("Не удалось сохранить подключение в Keychain") }
    }
    static func load() -> Pairing? {
        var item = query; item[kSecReturnData as String] = true; item[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(item as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return try? JSONDecoder().decode(Pairing.self, from: data)
    }
}
