import SwiftUI
import AVFoundation

@main
struct AirTakeCameraApp: App {
    @StateObject private var model = CameraModel()
    @Environment(\.scenePhase) private var phase
    var body: some Scene {
        WindowGroup {
            CameraView(model: model)
                .preferredColorScheme(.dark)
                .task { await model.activate() }
                .onChange(of: phase) { _, newPhase in if newPhase == .background { model.backgrounded() } }
        }
    }
}
struct CameraView: View {
    @ObservedObject var model: CameraModel
    @State private var manual = false
    var body: some View {
        HStack(spacing: 18) {
            ZStack(alignment: .bottomLeading) {
                CameraPreview(session: model.engine.session).background(.black)
                VStack(alignment: .leading) {
                    Text(model.recording ? "● REC" : "AIRTAKE CAMERA").bold().foregroundStyle(model.recording ? .red : .white)
                    Text("Основная камера 1× · горизонтально").font(.caption)
                }.padding(16).background(.black.opacity(0.45)).padding(12)
            }.clipShape(RoundedRectangle(cornerRadius: 16))
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    Text("AirTake").font(.largeTitle.bold())
                    Text(model.connection).font(.caption).foregroundStyle(.secondary)
                    HStack { stat("FPS", String(format: "%.1f", model.stats.fps)); stat("Пропуски", "\(model.stats.dropped)") }
                    Text("Передано в энкодер: \(model.stats.captured)\nЗаписано кадров: \(model.stats.encoded)\nБуфер: \(String(format: "%.1f", Double(model.bufferedBytes) / 1048576)) МиБ\nТемпература: \(model.thermalName)").font(.caption.monospacedDigit())
                    if let error = model.error { Text(error).font(.caption).foregroundStyle(.orange).textSelection(.enabled) }
                    if model.recording { Button("■  Остановить запись", role: .destructive) { model.stop() }.buttonStyle(.borderedProminent) }
                    else {
                        Button("Сканировать QR с ПК") { model.prepareScanner() }.buttonStyle(.borderedProminent)
                        Button("Вставить подключение") { manual = true }.buttonStyle(.bordered)
                        Text("Запускайте запись кнопкой на компьютере. FPS и битрейт задаются в Windows.").font(.caption).foregroundStyle(.secondary)
                    }
                    Toggle("Фиксация фокуса", isOn: $model.focusLocked)
                    Toggle("Фиксация экспозиции", isOn: $model.exposureLocked)
                    Toggle("Фиксация баланса белого", isOn: $model.whiteBalanceLocked)
                    Text("Не сворачивайте приложение до завершения передачи. Звук пишет Fifine на ПК, микрофон телефона не используется.").font(.caption2).foregroundStyle(.secondary)
                }.font(.subheadline)
            }.frame(width: 280)
        }.padding(16)
        .onChange(of: model.focusLocked) { _, _ in model.updateLocks() }
        .onChange(of: model.exposureLocked) { _, _ in model.updateLocks() }
        .onChange(of: model.whiteBalanceLocked) { _, _ in model.updateLocks() }
        .sheet(isPresented: $model.scanning) {
            QRScanner { text in model.scanning = false; Task { await model.connect(text: text) } }
        }
        .sheet(isPresented: $manual) {
            VStack {
                Text("Вставьте JSON из «Скопировать подключение» на ПК").font(.headline)
                TextEditor(text: $model.manualPairing).font(.caption.monospaced()).autocorrectionDisabled()
                HStack { Button("Отмена") { manual = false }; Button("Подключить") { manual = false; Task { await model.connect(text: model.manualPairing) } }.buttonStyle(.borderedProminent) }
            }.padding(24)
        }
    }
    private func stat(_ name: String, _ value: String) -> some View {
        VStack(alignment: .leading) { Text(name).font(.caption).foregroundStyle(.secondary); Text(value).font(.title2.monospacedDigit().bold()) }.frame(maxWidth: .infinity, alignment: .leading)
    }
}
struct CameraPreview: UIViewRepresentable {
    let session: AVCaptureSession
    final class Preview: UIView {
        override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }
        var previewLayer: AVCaptureVideoPreviewLayer { layer as! AVCaptureVideoPreviewLayer }
    }
    func makeUIView(context: Context) -> Preview { let view = Preview(); view.previewLayer.session = session; view.previewLayer.videoGravity = .resizeAspect; return view }
    func updateUIView(_ uiView: Preview, context: Context) {
        if let connection = uiView.previewLayer.connection, connection.isVideoRotationAngleSupported(0) { connection.videoRotationAngle = 0 }
    }
}
struct QRScanner: UIViewControllerRepresentable {
    var found: (String) -> Void
    func makeUIViewController(context: Context) -> ScannerController { ScannerController(found: found) }
    func updateUIViewController(_ uiViewController: ScannerController, context: Context) {}
    static func dismantleUIViewController(_ uiViewController: ScannerController, coordinator: ()) { uiViewController.stop() }
}
final class ScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    private let capture = AVCaptureSession()
    private let queue = DispatchQueue(label: "airtake.qr")
    private var layer: AVCaptureVideoPreviewLayer?
    private var delivered = false
    private let found: (String) -> Void
    init(found: @escaping (String) -> Void) { self.found = found; super.init(nibName: nil, bundle: nil) }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }
    override func viewDidLoad() {
        super.viewDidLoad(); view.backgroundColor = .black
        guard let device = AVCaptureDevice.default(for: .video), let input = try? AVCaptureDeviceInput(device: device), capture.canAddInput(input) else { return }
        capture.addInput(input)
        let output = AVCaptureMetadataOutput(); guard capture.canAddOutput(output) else { return }; capture.addOutput(output)
        output.setMetadataObjectsDelegate(self, queue: .main); output.metadataObjectTypes = [.qr]
        let preview = AVCaptureVideoPreviewLayer(session: capture); preview.videoGravity = .resizeAspectFill; view.layer.addSublayer(preview); layer = preview
        queue.async { [capture] in capture.startRunning() }
    }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); layer?.frame = view.bounds }
    func stop() { queue.async { [capture] in if capture.isRunning { capture.stopRunning() } } }
    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
        guard !delivered, let item = metadataObjects.first as? AVMetadataMachineReadableCodeObject, let value = item.stringValue else { return }
        delivered = true; stop(); found(value)
    }
}
