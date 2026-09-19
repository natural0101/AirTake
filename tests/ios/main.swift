import Foundation

struct TestError: Error { let message: String }
var passed = 0
var failures: [String] = []
let root = FileManager.default.temporaryDirectory.appendingPathComponent("AirTake-tests-" + UUID().uuidString)
try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
func expect(_ condition: Bool, _ message: String = "Assertion failed") throws { if !condition { throw TestError(message: message) } }
func rejects(_ action: () throws -> Void) throws { do { try action() } catch { return }; throw TestError(message: "Expected a rejection") }
func test(_ name: String, _ action: () throws -> Void) {
    do { try action(); passed += 1; print("PASS \(name)") }
    catch { failures.append("\(name): \(error)"); print("FAIL \(name): \(error)") }
}
func makeSpool() throws -> (DiskSpool, URL) {
    let path = root.appendingPathComponent(UUID().uuidString)
    let spool = try DiskSpool(directory: path)
    try spool.create(id: UUID().uuidString.lowercased(), options: CaptureOptions())
    return (spool, path)
}
let payload = Data("test-completed-segment".utf8)
test("queue does not accept a second active take") {
    let (s, _) = try makeSpool()
    try rejects { try s.create(id: UUID().uuidString, options: CaptureOptions()) }
}
test("segment persisted before becoming visible to uploader") {
    let (s, _) = try makeSpool(); try s.append(payload)
    let next = s.nextPending()!
    try expect(next.index == 0 && (try Data(contentsOf: next.url)) == payload)
    try expect(s.pendingBytes == Int64(payload.count))
}
test("unacknowledged recording cannot be deleted") {
    let (s, _) = try makeSpool(); try s.append(payload); try s.close(TakeEnd())
    try rejects { try s.clearCompleted() }
    try expect(s.nextPending() != nil)
}
test("ACK removes exactly the acknowledged segment") {
    let (s, _) = try makeSpool(); try s.append(payload); try s.append(payload)
    let first = s.nextPending()!.url
    try s.acknowledge(1)
    try expect(!FileManager.default.fileExists(atPath:first.path))
    try expect(s.nextPending()?.index == 1 && s.pendingBytes == Int64(payload.count))
}
test("duplicate ACK is idempotent") {
    let (s, _) = try makeSpool(); try s.append(payload); try s.acknowledge(1); try s.acknowledge(1)
    try expect(s.pendingBytes == 0)
}
test("future and negative ACK rejected") {
    let (s, _) = try makeSpool(); try s.append(payload)
    try rejects { try s.acknowledge(2) }; try rejects { try s.acknowledge(-1) }
    try expect(s.nextPending()?.index == 0)
}
test("process restart preserves pending data and marks interruption") {
    let (s, path) = try makeSpool(); try s.append(payload)
    let reopened = try DiskSpool(directory: path)
    try expect(reopened.metadata?.closed == true && reopened.metadata?.end.interrupted == true)
    try expect((try Data(contentsOf: reopened.nextPending()!.url)) == payload)
}
test("ACK journal survives process restart") {
    let (s, path) = try makeSpool(); try s.append(payload); try s.append(payload); try s.acknowledge(1)
    let reopened = try DiskSpool(directory: path)
    try expect(reopened.nextPending()?.index == 1 && reopened.pendingBytes == Int64(payload.count))
}
test("finished and fully acknowledged queue clears") {
    let (s, _) = try makeSpool(); try s.append(payload); try s.close(TakeEnd()); try s.acknowledge(1); try s.clearCompleted()
    try expect(s.metadata == nil && s.pendingBytes == 0)
}
test("closed queue rejects new frames") {
    let (s, _) = try makeSpool(); try s.close(TakeEnd()); try rejects { try s.append(payload) }
}
test("empty fragments rejected") { let (s, _) = try makeSpool(); try rejects { try s.append(Data()) } }
test("pairing validates schema, pin, port and IPv4") {
    let code = "airtake://pair?host=192.168.1.10&port=48721&token=\(String(repeating:"a",count:64))&pin=\(String(repeating:"b",count:64))&v=1"
    let pairing = try Pairing(code); try expect(pairing.host == "192.168.1.10")
    try rejects { _ = try Pairing(code.replacingOccurrences(of:"v=1",with:"v=2")) }
    try rejects { _ = try Pairing(code.replacingOccurrences(of:"192.168.1.10",with:"evil.example.com")) }
    try rejects { _ = try Pairing(code.replacingOccurrences(of:"port=48721",with:"port=80")) }
}
print("RESULT: \(passed) passed, \(failures.count) failed")
let report: [String: Any] = ["passed":passed,"failed":failures.count,"failures":failures]
try FileManager.default.createDirectory(atPath:"qa",withIntermediateDirectories:true)
try JSONSerialization.data(withJSONObject:report,options:.prettyPrinted).write(to:URL(fileURLWithPath:"qa/ios-spool-tests.json"))
try? FileManager.default.removeItem(at: root)
exit(failures.isEmpty ? 0 : 1)
