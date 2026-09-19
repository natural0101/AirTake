import AVFoundation
import SwiftUI

@main
struct AirTakeCameraApp: App {
    @StateObject private var controller = CaptureController()
    var body: some Scene { WindowGroup { CameraScreen(model: controller).preferredColorScheme(.dark) } }
}

struct CameraScreen: View {
    @ObservedObject var model: CaptureController
    @Environment(\.scenePhase) private var scenePhase
    @State private var scanner = false
    @State private var scannedCode: String?
    @State private var pairCode = ""
    var body: some View {
        HStack(spacing: 0) {
            ZStack(alignment: .bottomLeading) {
                CameraPreview(session: model.camera.session).background(Color.black)
                VStack(alignment: .leading, spacing: 7) {
                    Text(model.mode).font(.system(.headline, design: .monospaced))
                    Text(model.recording ? "REC  ·  HEVC → Windows" : "Основная камера 1×").foregroundStyle(model.recording ? .red : .white)
                }.padding(16).background(.black.opacity(0.6)).padding(16)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 13) {
                    Text("AirTake").font(.largeTitle.bold()).foregroundStyle(.mint)
                    Text(model.receiverName).font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
                    Text(model.status).font(.subheadline)
                    HStack {
                        metric("FPS", String(format: "%.1f", model.measuredFps))
                        metric("ПРОПУСКИ", String(model.dropped))
                        metric("БУФЕР", String(format: "%.1f МиБ", Double(model.pendingBytes) / 1048576))
                    }
                    Text("HEVC HW: \(model.hardware ? "подтверждён" : "не проверен") · нагрев: \(model.thermal)").font(.caption).foregroundStyle(.secondary)
                    Button { Task { await model.requestRecording() } } label: {
                        Text(model.recording ? "ОСТАНОВИТЬ" : "REC / ЗАПИСАТЬ").font(.headline).frame(maxWidth: .infinity).padding(.vertical, 10)
                    }.buttonStyle(.borderedProminent).tint(model.recording ? .red : .mint)
                        .disabled((!model.connected && !model.recording) || (model.working && !model.recording) || (model.hasPendingTake && !model.recording))
                    HStack {
                        Button("Сканировать QR") { Task { await model.prepareScanner(); if model.scanning { scanner = true } } }
                        Button("Тест Wi-Fi") { Task { await model.testNetwork() } }
                    }.buttonStyle(.bordered).disabled(model.recording || model.working)
                    if !model.speed.isEmpty { Text(model.speed).font(.caption).foregroundStyle(.mint) }
                    TextField("Код airtake:// из приложения ПК", text: $pairCode).textFieldStyle(.roundedBorder).textInputAutocapitalization(.never).autocorrectionDisabled()
                    Button("Подключить по коду") { Task { await model.connect(pairCode) } }.disabled(pairCode.isEmpty || model.recording || model.working)
                    if model.hasPendingTake { Button("Повторить передачу") { model.retryUpload() }.buttonStyle(.bordered) }
                    if !model.errorText.isEmpty { Text(model.errorText).font(.caption).foregroundStyle(.orange).textSelection(.enabled) }
                    Text("Разрешение, FPS, битрейт, экспозиция, фокус и баланс белого задаются в Windows-приложении. Звук — с Fifine на ПК. Не сворачивайте приложение во время записи.").font(.caption).foregroundStyle(.secondary)
                }.padding(18)
            }.frame(width: 345).background(Color(red: 0.07, green: 0.09, blue: 0.13))
        }.ignoresSafeArea(edges: .bottom)
            .task { await model.bootstrap() }
            .onChange(of: scenePhase) { _, phase in if phase != .active { Task { await model.backgrounded() } } }
            .fullScreenCover(isPresented: $scanner, onDismiss: {
                model.finishScanner()
                if let code = scannedCode { scannedCode = nil; Task { await model.connect(code) } }
            }) {
                QRScanner { code in scannedCode = code; scanner = false }
                    .overlay(alignment: .topTrailing) { Button("Отмена") { scannedCode = nil; scanner = false }.buttonStyle(.borderedProminent).padding(24) }
            }
    }
    private func metric(_ title: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 9, weight: .semibold)).foregroundStyle(.secondary)
            Text(value).font(.system(.subheadline, design: .monospaced).bold())
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
}
private final class PreviewView: UIView {
    override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }
    var previewLayer: AVCaptureVideoPreviewLayer { layer as! AVCaptureVideoPreviewLayer }
}
struct CameraPreview: UIViewRepresentable {
    let session: AVCaptureSession
    func makeUIView(context: Context) -> UIView {
        let view = PreviewView(); view.previewLayer.session = session; view.previewLayer.videoGravity = .resizeAspect
        if view.previewLayer.connection?.isVideoOrientationSupported == true { view.previewLayer.connection?.videoOrientation = .landscapeRight }
        return view
    }
    func updateUIView(_ uiView: UIView, context: Context) { }
}
