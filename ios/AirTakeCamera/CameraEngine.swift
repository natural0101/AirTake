import AVFoundation
import CoreImage
import Foundation
import UniformTypeIdentifiers
import VideoToolbox

final class CameraEngine: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate, AVAssetWriterDelegate {
    let session = AVCaptureSession()
    private let captureQueue = DispatchQueue(label: "AirTake.capture", qos: .userInitiated)
    private let encodeQueue = DispatchQueue(label: "AirTake.encoded", qos: .userInitiated)
    private let statsLock = NSLock()
    private let previewLock = NSLock()
    private let imageContext = CIContext(options: [.cacheIntermediates: false])
    private var compressor: VTCompressionSession?
    private var writer: AVAssetWriter?
    private var writerInput: AVAssetWriterInput?
    private var options = CaptureOptions()
    private var recording = false
    private var firstPTS: CMTime?
    private var snapshotValue = CameraSnapshot()
    private var segmentSink: ((Data) throws -> Void)?
    private var previewData: Data?
    private var lastPreviewTime = 0.0
    private var clockOffset = 0.0
    private var failureReported = false
    private var encodedQueueCount = 0
    private var observers: [NSObjectProtocol] = []
    var onFailure: ((String) -> Void)?

    override init() {
        super.init()
        for name in [AVCaptureSession.wasInterruptedNotification, AVCaptureSession.runtimeErrorNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: session, queue: nil) { [weak self] _ in
                guard let self else { return }
                self.captureQueue.async { if self.recording { self.fail("Система прервала работу камеры. Завершённые фрагменты сохраняются.") } }
            })
        }
    }
    deinit { observers.forEach(NotificationCenter.default.removeObserver) }
    var snapshot: CameraSnapshot { statsLock.lock(); defer { statsLock.unlock() }; return snapshotValue }
    private func stats(_ update: (inout CameraSnapshot) -> Void) { statsLock.lock(); update(&snapshotValue); statsLock.unlock() }
    func takePreview() -> Data? { previewLock.lock(); defer { previewLock.unlock() }; let data = previewData; previewData = nil; return data }
    static func formats() -> [String] {
        guard let camera = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back) else { return [] }
        var values = Set<String>()
        for format in camera.formats {
            let size = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            guard (size.width == 3840 && size.height == 2160) || (size.width == 1920 && size.height == 1080) else { continue }
            for fps in [30,60,120] where format.videoSupportedFrameRateRanges.contains(where: { $0.minFrameRate <= Double(fps) && $0.maxFrameRate >= Double(fps) }) { values.insert("\(size.width)x\(size.height) / \(fps)") }
        }
        return values.sorted()
    }
    func configure(_ requested: CaptureOptions) async throws {
        guard await AVCaptureDevice.requestAccess(for: .video) else { throw AirTakeError.message("Разрешите доступ к камере в настройках iPhone.") }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            captureQueue.async {
                do { try self.configureOnQueue(requested); continuation.resume() }
                catch { continuation.resume(throwing: error) }
            }
        }
    }
    private func configureOnQueue(_ requested: CaptureOptions) throws {
        guard !recording else { throw AirTakeError.message("Нельзя менять формат во время записи.") }
        guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back) else { throw AirTakeError.message("Основная задняя камера недоступна.") }
        let candidates = device.formats.filter { format in
            let size = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            return size.width == Int32(requested.width) && size.height == Int32(requested.height) && format.videoSupportedFrameRateRanges.contains { $0.minFrameRate <= Double(requested.fps) && $0.maxFrameRate >= Double(requested.fps) }
        }
        let format = candidates.first { CMFormatDescriptionGetMediaSubType($0.formatDescription) == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange } ?? candidates.first
        guard let format else { throw AirTakeError.message("Эта камера не предоставляет настоящий режим \(requested.width)x\(requested.height)/\(requested.fps). Выберите поддерживаемый режим на ПК. Подмена FPS отключена.") }
        if session.isRunning { session.stopRunning() }
        session.beginConfiguration()
        defer { session.commitConfiguration() }
        session.inputs.forEach(session.removeInput)
        session.outputs.forEach(session.removeOutput)
        session.sessionPreset = .inputPriority
        let input = try AVCaptureDeviceInput(device: device)
        guard session.canAddInput(input) else { throw AirTakeError.message("Не удалось подключить камеру.") }
        session.addInput(input)
        let output = AVCaptureVideoDataOutput()
        output.alwaysDiscardsLateVideoFrames = true
        guard session.canAddOutput(output) else { throw AirTakeError.message("Не удалось создать видеовыход.") }
        session.addOutput(output)
        let pixelFormat = output.availableVideoPixelFormatTypes.contains(kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange) ? kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange : kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
        output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: pixelFormat]
        output.setSampleBufferDelegate(self, queue: captureQueue)
        if let connection = output.connection(with: .video) {
            if connection.isVideoStabilizationSupported { connection.preferredVideoStabilizationMode = .off }
            if connection.isVideoOrientationSupported { connection.videoOrientation = .landscapeRight }
        }
        try device.lockForConfiguration()
        defer { device.unlockForConfiguration() }
        device.activeFormat = format
        let duration = CMTime(value: 1, timescale: CMTimeScale(requested.fps))
        device.activeVideoMinFrameDuration = duration
        device.activeVideoMaxFrameDuration = duration
        device.videoZoomFactor = 1
        if device.isExposureModeSupported(.continuousAutoExposure) { device.exposureMode = .continuousAutoExposure }
        device.setExposureTargetBias(min(device.maxExposureTargetBias, max(device.minExposureTargetBias, Float(requested.exposureBias))), completionHandler: nil)
        if requested.focus >= 0, device.isFocusModeSupported(.locked) { device.setFocusModeLocked(lensPosition: Float(requested.focus), completionHandler: nil) }
        else if device.isFocusModeSupported(.continuousAutoFocus) { device.focusMode = .continuousAutoFocus }
        if requested.whiteBalanceKelvin > 0, device.isWhiteBalanceModeSupported(.locked) {
            var gains = device.deviceWhiteBalanceGains(for: .init(temperature: Float(requested.whiteBalanceKelvin), tint: 0))
            gains.redGain = min(device.maxWhiteBalanceGain, max(1, gains.redGain))
            gains.greenGain = min(device.maxWhiteBalanceGain, max(1, gains.greenGain))
            gains.blueGain = min(device.maxWhiteBalanceGain, max(1, gains.blueGain))
            device.setWhiteBalanceModeLocked(with: gains, completionHandler: nil)
        } else if device.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance) { device.whiteBalanceMode = .continuousAutoWhiteBalance }
        options = requested
        stats { $0.mode = "\(requested.width)x\(requested.height) / \(requested.fps)" }
        captureQueue.async { if !self.session.isRunning { self.session.startRunning() } }
    }
    func pausePreview() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            captureQueue.async { if !self.recording && self.session.isRunning { self.session.stopRunning() }; continuation.resume() }
        }
    }
    func arm() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            captureQueue.async {
                do { try self.armOnQueue(); continuation.resume() } catch { continuation.resume(throwing: error) }
            }
        }
    }
    private func armOnQueue() throws {
        if let compressor { VTCompressionSessionInvalidate(compressor); self.compressor = nil }
        statsLock.lock(); failureReported = false; encodedQueueCount = 0; statsLock.unlock()
        firstPTS = nil
        encodeQueue.sync { writer = nil; writerInput = nil }
        stats { $0 = CameraSnapshot(); $0.mode = "\(options.width)x\(options.height) / \(options.fps)" }
        let specification = [kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder: true] as CFDictionary
        let status = VTCompressionSessionCreate(allocator: kCFAllocatorDefault, width: Int32(options.width), height: Int32(options.height), codecType: kCMVideoCodecType_HEVC, encoderSpecification: specification, imageBufferAttributes: nil, compressedDataAllocator: nil, outputCallback: { refcon, _, status, flags, sample in
            guard let refcon else { return }
            let engine = Unmanaged<CameraEngine>.fromOpaque(refcon).takeUnretainedValue()
            if status != noErr { engine.fail("HEVC encoder error: \(status)"); return }
            if flags.contains(.frameDropped) { engine.stats { $0.end.encoderDrops += 1 }; return }
            guard let sample else { engine.fail("HEVC encoder returned an empty sample."); return }
            engine.statsLock.lock()
            engine.encodedQueueCount += 1
            let overflow = engine.encodedQueueCount > 240
            engine.statsLock.unlock()
            if overflow {
                engine.statsLock.lock(); engine.encodedQueueCount -= 1; engine.statsLock.unlock()
                engine.stats { $0.end.writerDrops += 1 }
                engine.fail("Очередь MP4 не успевает за камерой. Запись остановлена без снижения FPS.")
                return
            }
            engine.encodeQueue.async {
                engine.appendEncoded(sample)
                engine.statsLock.lock(); engine.encodedQueueCount -= 1; engine.statsLock.unlock()
            }
        }, refcon: Unmanaged.passUnretained(self).toOpaque(), compressionSessionOut: &compressor)
        guard status == noErr, let compressor else { throw AirTakeError.message("Аппаратный HEVC-энкодер не открылся для этого режима: \(status).") }
        do {
            try set(compressor, kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
            try set(compressor, kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
            try set(compressor, kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_HEVC_Main_AutoLevel)
            try set(compressor, kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: options.bitrateMbps * 1_000_000))
            try set(compressor, kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: options.fps))
            try set(compressor, kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: options.fps))
            try set(compressor, kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, NSNumber(value: 1))
            let prepared = VTCompressionSessionPrepareToEncodeFrames(compressor)
            guard prepared == noErr else { throw AirTakeError.message("HEVC prepare failed: \(prepared)") }
            var hardware: CFTypeRef?
            let queried = VTSessionCopyProperty(compressor, key: kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder, allocator: kCFAllocatorDefault, valueOut: &hardware)
            guard queried == noErr, (hardware as? NSNumber)?.boolValue == true else { throw AirTakeError.message("Аппаратное кодирование не подтверждено. Программный fallback запрещён.") }
            stats { $0.hardware = true }
        } catch { VTCompressionSessionInvalidate(compressor); self.compressor = nil; throw error }
    }
    private func set(_ session: VTCompressionSession, _ key: CFString, _ value: CFTypeRef) throws {
        let status = VTSessionSetProperty(session, key: key, value: value)
        guard status == noErr else { throw AirTakeError.message("HEVC \(key): \(status)") }
    }
    func begin(offset: Double, rtt: Double, sink: @escaping (Data) throws -> Void) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            captureQueue.async {
                self.clockOffset = offset; self.segmentSink = sink; self.recording = true
                self.stats { $0.end.clockRttMs = rtt }
                continuation.resume()
            }
        }
    }
    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard let pixels = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        if options.preview, PhoneClock.now - lastPreviewTime >= 1 {
            lastPreviewTime = PhoneClock.now
            let image = CIImage(cvPixelBuffer: pixels)
            let scale = 640 / image.extent.width
            let reduced = image.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
            if let data = imageContext.jpegRepresentation(of: reduced, colorSpace: CGColorSpaceCreateDeviceRGB(), options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: 0.65]) {
                previewLock.lock(); previewData = data; previewLock.unlock()
            }
        }
        guard recording, let compressor else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        if firstPTS == nil {
            firstPTS = pts
            let host = CMClockGetHostTimeClock()
            let sampleHost = session.masterClock.map { CMSyncConvertTime(pts, from: $0, to: host) } ?? pts
            let lag = max(0, CMTimeGetSeconds(CMTimeSubtract(CMClockGetTime(host), sampleHost)))
            stats { $0.end.videoStartUnix = PhoneClock.now - min(lag, 1) + clockOffset }
        }
        let relative = CMTimeSubtract(pts, firstPTS!)
        let duration = CMTime(value: 1, timescale: CMTimeScale(options.fps))
        stats { $0.end.captured += 1; $0.end.duration = CMTimeGetSeconds(relative) + CMTimeGetSeconds(duration) }
        let status = VTCompressionSessionEncodeFrame(compressor, imageBuffer: pixels, presentationTimeStamp: relative, duration: duration, frameProperties: nil, sourceFrameRefcon: nil, infoFlagsOut: nil)
        if status != noErr { stats { $0.end.encoderDrops += 1 }; fail("HEVC frame submission failed: \(status)") }
    }
    func captureOutput(_ output: AVCaptureOutput, didDrop sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        if recording { stats { $0.end.captureDrops += 1 } }
    }
    private func appendEncoded(_ sample: CMSampleBuffer) {
        do {
            if writer == nil {
                let asset = AVAssetWriter(contentType: .mpeg4Movie)
                asset.outputFileTypeProfile = .mpeg4AppleHLS
                asset.preferredOutputSegmentInterval = CMTime(value: 1, timescale: 1)
                asset.initialSegmentStartTime = .zero
                asset.delegate = self
                let input = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: CMSampleBufferGetFormatDescription(sample))
                input.expectsMediaDataInRealTime = true
                guard asset.canAdd(input) else { throw AirTakeError.message("HEVC passthrough input was rejected.") }
                asset.add(input)
                guard asset.startWriting() else { throw asset.error ?? AirTakeError.message("MP4 writer could not start.") }
                asset.startSession(atSourceTime: .zero)
                writer = asset; writerInput = input
            }
            guard let writerInput, writerInput.isReadyForMoreMediaData else { stats { $0.end.writerDrops += 1 }; throw AirTakeError.message("MP4 writer cannot keep up; recording is stopped, not silently downgraded.") }
            guard writerInput.append(sample) else { stats { $0.end.writerDrops += 1 }; throw writer?.error ?? AirTakeError.message("Could not append HEVC sample.") }
            stats { $0.end.encoded += 1 }
        } catch { fail(error.localizedDescription) }
    }
    func assetWriter(_ writer: AVAssetWriter, didOutputSegmentData segmentData: Data, segmentType: AVAssetSegmentType) {
        do { guard let segmentSink else { throw AirTakeError.message("No segment store.") }; try segmentSink(segmentData) }
        catch { stats { $0.end.interrupted = true }; fail(error.localizedDescription) }
    }
    private func fail(_ text: String) {
        statsLock.lock()
        let shouldReport = !failureReported
        failureReported = true; snapshotValue.end.error = text; snapshotValue.end.interrupted = true
        statsLock.unlock()
        if shouldReport { onFailure?(text) }
    }
    func finish(error: String = "") async -> TakeEnd {
        await withCheckedContinuation { (continuation: CheckedContinuation<TakeEnd, Never>) in
            captureQueue.async {
                self.recording = false
                if !error.isEmpty { self.stats { $0.end.interrupted = true; $0.end.error = error } }
                if let compressor = self.compressor {
                    let result = VTCompressionSessionCompleteFrames(compressor, untilPresentationTimeStamp: .invalid)
                    if result != noErr { self.stats { $0.end.interrupted = true; $0.end.error = "HEVC flush failed: \(result)" } }
                    VTCompressionSessionInvalidate(compressor); self.compressor = nil
                }
                if self.session.isRunning { self.session.stopRunning() }
                self.encodeQueue.async {
                    guard let writer = self.writer else { continuation.resume(returning: self.snapshot.end); return }
                    self.writerInput?.markAsFinished()
                    writer.finishWriting {
                        if let error = writer.error { self.stats { $0.end.interrupted = true; $0.end.error = error.localizedDescription } }
                        continuation.resume(returning: self.snapshot.end)
                    }
                }
            }
        }
    }
}
