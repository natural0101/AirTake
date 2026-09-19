import Foundation
import CryptoKit

struct CaptureOptions: Codable, Equatable {
    var width = 3840
    var height = 2160
    var fps = 120
    var bitrateMbps = 120
    var bufferMiB = 512
    var preview = false
    var exposureBias = 0.0
    var focus = -1.0
    var whiteBalanceKelvin = 0
}
struct ControlReply: Decodable {
    let version: Int
    let recording: Bool
    let capture: CaptureOptions
    let activeId: String?
    let status: String
}
struct TakeEnd: Codable {
    var videoStartUnix = 0.0
    var duration = 0.0
    var clockRttMs = 0.0
    var captured: Int64 = 0
    var encoded: Int64 = 0
    var captureDrops: Int64 = 0
    var encoderDrops: Int64 = 0
    var writerDrops: Int64 = 0
    var interrupted = false
    var error = ""
}
struct PhoneTelemetry: Codable {
    var clientId = ""
    var status = "ready"
    var message = ""
    var mode = ""
    var fps = 0.0
    var captured: Int64 = 0
    var dropped: Int64 = 0
    var pendingBytes: Int64 = 0
    var battery = -1.0
    var thermal = "unknown"
    var hardwareEncoder = false
    var formats: [String] = []
}
struct CameraSnapshot {
    var end = TakeEnd()
    var hardware = false
    var mode = ""
    var fps: Double { end.duration > 0 ? Double(end.captured) / end.duration : 0 }
}
struct Pairing: Equatable {
    let host: String
    let port: Int
    let token: String
    let pin: String
    let original: String
    init(_ text: String) throws {
        guard let components = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
              components.scheme == "airtake", components.host == "pair" else { throw AirTakeError.message("Нужен QR-код или код airtake:// из Windows-приложения.") }
        func value(_ key: String) -> String? { components.queryItems?.first(where: { $0.name == key })?.value }
        guard value("v") == "1", let host = value("host"), let portText = value("port"), let port = Int(portText), (1024...65535).contains(port),
              let token = value("token"), token.count == 64, token.allSatisfy({ $0.isHexDigit }),
              let pin = value("pin"), pin.count == 64, pin.allSatisfy({ $0.isHexDigit }),
              host.split(separator: ".").count == 4, host.split(separator: ".").allSatisfy({ UInt8($0) != nil }) else { throw AirTakeError.message("Некорректный код сопряжения.") }
        self.host = host; self.port = port; self.token = token; self.pin = pin.lowercased(); self.original = text
    }
    func url(_ path: String) throws -> URL {
        guard path.hasPrefix("/v1/"), let url = URL(string: "https://\(host):\(port)\(path)") else { throw AirTakeError.message("Некорректный адрес API.") }
        return url
    }
}
enum AirTakeError: LocalizedError {
    case message(String)
    case http(Int, String)
    var errorDescription: String? {
        switch self { case .message(let text): return text; case .http(let code, let text): return "HTTP \(code): \(text)" }
    }
}
enum PhoneClock {
    private static let epoch = Date().timeIntervalSince1970
    private static let start = ProcessInfo.processInfo.systemUptime
    static var now: Double { epoch + ProcessInfo.processInfo.systemUptime - start }
}
func sha256(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
