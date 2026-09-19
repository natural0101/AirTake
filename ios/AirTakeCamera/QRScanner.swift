import AVFoundation
import SwiftUI

struct QRScanner: UIViewControllerRepresentable {
    let onCode: (String) -> Void
    func makeUIViewController(context: Context) -> ScannerController { ScannerController(onCode: onCode) }
    func updateUIViewController(_ uiViewController: ScannerController, context: Context) { }
}

final class ScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    private let capture = AVCaptureSession()
    private let queue = DispatchQueue(label: "AirTake.QR")
    private let preview = AVCaptureVideoPreviewLayer()
    private let onCode: (String) -> Void
    private var found = false
    init(onCode: @escaping (String) -> Void) { self.onCode = onCode; super.init(nibName: nil, bundle: nil) }
    required init?(coder: NSCoder) { fatalError("Storyboard initialization is not supported.") }
    override func viewDidLoad() {
        super.viewDidLoad(); view.backgroundColor = .black
        preview.session = capture; preview.videoGravity = .resizeAspectFill; view.layer.addSublayer(preview)
        AVCaptureDevice.requestAccess(for: .video) { [weak self] allowed in
            guard let self, allowed else { return }
            self.queue.async {
                do {
                    guard let device = AVCaptureDevice.default(for: .video) else { return }
                    let input = try AVCaptureDeviceInput(device: device)
                    let output = AVCaptureMetadataOutput()
                    self.capture.beginConfiguration()
                    guard self.capture.canAddInput(input) else { self.capture.commitConfiguration(); return }
                    self.capture.addInput(input)
                    guard self.capture.canAddOutput(output) else { self.capture.commitConfiguration(); return }
                    self.capture.addOutput(output); output.setMetadataObjectsDelegate(self, queue: .main); output.metadataObjectTypes = [.qr]
                    self.capture.commitConfiguration(); self.capture.startRunning()
                    DispatchQueue.main.async { if self.preview.connection?.isVideoOrientationSupported == true { self.preview.connection?.videoOrientation = .landscapeRight } }
                } catch { DispatchQueue.main.async { self.onCode("") } }
            }
        }
    }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); preview.frame = view.bounds }
    override func viewWillDisappear(_ animated: Bool) { super.viewWillDisappear(animated); queue.async { if self.capture.isRunning { self.capture.stopRunning() } } }
    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput objects: [AVMetadataObject], from connection: AVCaptureConnection) {
        guard !found, let code = objects.compactMap({ ($0 as? AVMetadataMachineReadableCodeObject)?.stringValue }).first(where: { $0.hasPrefix("airtake://") }) else { return }
        found = true
        queue.async { self.capture.stopRunning(); DispatchQueue.main.async { self.onCode(code) } }
    }
}
