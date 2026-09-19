import Foundation
import Security

final class ReceiverAPI: NSObject, URLSessionDelegate, URLSessionTaskDelegate, @unchecked Sendable {
    let pairing: Pairing
    private var session: URLSession!
    init(pairing: Pairing) throws {
        try pairing.validate(); self.pairing = pairing; super.init()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        configuration.timeoutIntervalForResource = 600
        configuration.httpMaximumConnectionsPerHost = 4
        configuration.waitsForConnectivity = false
        configuration.urlCache = nil
        session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
    }
    func close() { session.invalidateAndCancel() }
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              challenge.protectionSpace.host == URL(string: pairing.url)?.host,
              let trust = challenge.protectionSpace.serverTrust,
              let certificate = SecTrustGetCertificateAtIndex(trust, 0),
              sha256(SecCertificateCopyData(certificate) as Data) == pairing.fingerprint.lowercased()
        else { completionHandler(.cancelAuthenticationChallenge, nil); return }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        // No automatic redirects: never leak the bearer secret to a different endpoint.
        completionHandler(nil)
    }
    private func request(_ path: String, method: String = "GET") throws -> URLRequest {
        guard let url = URL(string: pairing.url + path) else { throw AirError.message("Некорректный адрес") }
        var r = URLRequest(url: url); r.httpMethod = method
        r.setValue("Bearer " + pairing.token, forHTTPHeaderField: "Authorization")
        return r
    }
    private func decode<T: Decodable>(_ result: (Data, URLResponse), as type: T.Type) throws -> T {
        guard let response = result.1 as? HTTPURLResponse else { throw AirError.message("Нет ответа приёмника") }
        guard (200...299).contains(response.statusCode) else {
            let detail = String(data: result.0.prefix(1024), encoding: .utf8) ?? ""
            throw AirError.message("Приёмник: HTTP \(response.statusCode). \(detail)")
        }
        return try JSONDecoder().decode(T.self, from: result.0)
    }
    func time() async throws -> Double {
        try decode(await session.data(for: request("/api/time")), as: TimeReply.self).serverTimeMs
    }
    func synchronizeClock() async throws -> Double {
        var bestRTT = Double.infinity, offset = 0.0
        for _ in 0..<7 {
            let before = hostTimeMs(), server = try await time(), after = hostTimeMs()
            if after - before < bestRTT { bestRTT = after - before; offset = server - (before + after) / 2 }
        }
        return offset
    }
    func status(_ status: PhoneStatus) async throws -> Control {
        var r = try request("/api/status", method: "POST"); r.setValue("application/json", forHTTPHeaderField: "Content-Type")
        r.httpBody = try JSONEncoder().encode(status)
        return try decode(await session.data(for: r), as: Control.self)
    }
    func upload(id: UUID, sequence: Int, file: URL, hash: String) async throws {
        var r = try request("/api/takes/\(id.uuidString)/chunks/\(sequence)", method: "PUT")
        r.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        r.setValue(hash, forHTTPHeaderField: "X-Content-SHA256")
        let size = try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
        r.setValue(String(size), forHTTPHeaderField: "Content-Length")
        let receipt = try decode(await session.upload(for: r, fromFile: file), as: Receipt.self)
        guard receipt.sequence == sequence, receipt.bytes == Int64(size), receipt.sha256 == hash else { throw AirError.message("Неверное подтверждение фрагмента") }
    }
    func complete(id: UUID, completion: Completion) async throws {
        var r = try request("/api/takes/\(id.uuidString)/complete", method: "POST")
        r.timeoutInterval = 600
        r.setValue("application/json", forHTTPHeaderField: "Content-Type"); r.httpBody = try JSONEncoder().encode(completion)
        guard try decode(await session.data(for: r), as: Success.self).ok else { throw AirError.message("Дубль не подтверждён") }
    }
}
