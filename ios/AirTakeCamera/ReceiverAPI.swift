import Foundation
import Security

final class ReceiverAPI: NSObject, URLSessionDelegate {
    let pairing: Pairing
    private var session: URLSession!
    init(pairing: Pairing) {
        self.pairing = pairing
        super.init()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 300
        configuration.httpMaximumConnectionsPerHost = 4
        configuration.waitsForConnectivity = false
        session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
    }
    func close() { session.invalidateAndCancel() }
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust else { completionHandler(.performDefaultHandling, nil); return }
        guard challenge.protectionSpace.host == pairing.host, challenge.protectionSpace.port == pairing.port,
              let trust = challenge.protectionSpace.serverTrust, let certificate = SecTrustGetCertificateAtIndex(trust, 0),
              sha256(SecCertificateCopyData(certificate) as Data) == pairing.pin else { completionHandler(.cancelAuthenticationChallenge, nil); return }
        // Trust ONLY the certificate explicitly pinned by the user's pairing QR.
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
    private func request(_ path: String, method: String) throws -> URLRequest {
        var request = URLRequest(url: try pairing.url(path))
        request.httpMethod = method
        request.setValue("Bearer \(pairing.token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        return request
    }
    private func check(_ data: Data, _ response: URLResponse) throws -> Data {
        guard let http = response as? HTTPURLResponse else { throw AirTakeError.message("Некорректный ответ ПК.") }
        guard (200...299).contains(http.statusCode) else {
            let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            throw AirTakeError.http(http.statusCode, object?["error"] as? String ?? String(data: data, encoding: .utf8) ?? "Ошибка передачи")
        }
        return data
    }
    func get<T: Decodable>(_ path: String, as type: T.Type) async throws -> T {
        let (data, response) = try await session.data(for: request(path, method: "GET"))
        return try JSONDecoder().decode(type, from: check(data, response))
    }
    @discardableResult func post<T: Encodable>(_ path: String, _ value: T) async throws -> Data {
        var request = try request(path, method: "POST")
        let body = try JSONEncoder().encode(value)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(String(body.count), forHTTPHeaderField: "Content-Length")
        request.httpBody = body
        if path.hasSuffix("/finish") { request.timeoutInterval = 300 }
        let (data, response) = try await session.data(for: request)
        return try check(data, response)
    }
    func upload(id: String, index: Int, url: URL) async throws -> Int {
        let data = try Data(contentsOf: url, options: .mappedIfSafe)
        guard data.count > 0, data.count <= 64 * 1024 * 1024 else { throw AirTakeError.message("Размер фрагмента выходит за пределы протокола.") }
        var request = try request("/v1/takes/\(id)/chunks/\(index)", method: "POST")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue(String(data.count), forHTTPHeaderField: "Content-Length")
        request.setValue(sha256(data), forHTTPHeaderField: "X-SHA256")
        let (reply, response) = try await session.upload(for: request, fromFile: url)
        struct Acknowledgement: Decodable { let nextIndex: Int }
        return try JSONDecoder().decode(Acknowledgement.self, from: check(reply, response)).nextIndex
    }
    func sendPreview(_ data: Data) async throws {
        var request = try request("/v1/preview", method: "POST")
        request.setValue("image/jpeg", forHTTPHeaderField: "Content-Type")
        request.setValue(String(data.count), forHTTPHeaderField: "Content-Length")
        let (reply, response) = try await session.upload(for: request, from: data)
        _ = try check(reply, response)
    }
    func syncClock() async throws -> (offset: Double, rtt: Double) {
        struct Reply: Decodable { let unixSeconds: Double; let version: Int }
        var best = (offset: 0.0, rtt: Double.infinity)
        for _ in 0..<5 {
            let before = PhoneClock.now
            let reply = try await get("/v1/clock", as: Reply.self)
            let after = PhoneClock.now
            guard reply.version == 1 else { throw AirTakeError.message("Версии приложений несовместимы.") }
            if after - before < best.rtt { best = (reply.unixSeconds - (before + after) / 2, after - before) }
        }
        return (best.offset, best.rtt * 1000)
    }
    func speedTest() async throws -> Double {
        // Fixed, bounded test payload. Never allocate raw 4K frame queues.
        let payload = Data(repeating: 0xA5, count: 32 * 1024 * 1024)
        var request = try request("/v1/speed", method: "POST")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue(String(payload.count), forHTTPHeaderField: "Content-Length")
        let before = PhoneClock.now
        let (reply, response) = try await session.upload(for: request, from: payload)
        _ = try check(reply, response)
        return Double(payload.count) * 8 / max(PhoneClock.now - before, 0.001) / 1_000_000
    }
}

enum PairingKeychain {
    private static var query: [String: Any] { [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "AirTakePairing", kSecAttrAccount as String: "receiver"] }
    static func read() -> String? {
        var request = query; request[kSecReturnData as String] = true; request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(request as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
    static func write(_ text: String) throws {
        let data = Data(text.utf8)
        let update = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if update == errSecItemNotFound {
            var entry = query; entry[kSecValueData as String] = data; entry[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            guard SecItemAdd(entry as CFDictionary, nil) == errSecSuccess else { throw AirTakeError.message("Не удалось сохранить сопряжение в Keychain.") }
        } else if update != errSecSuccess { throw AirTakeError.message("Не удалось обновить Keychain.") }
    }
}
