import AVFoundation
import VideoToolbox
import CoreImage
import UIKit
import UniformTypeIdentifiers

struct CameraStats {
    var captured: Int64 = 0, encoded: Int64 = 0, outputDrops: Int64 = 0, timestampGaps: Int64 = 0
    var fps = 0.0, firstVideoTimeMs = 0.0, durationSeconds = 0.0
    var dropped: Int64 { max(outputDrops, timestampGaps) }
}

final class CameraEngine: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate, AVAssetWriterDelegate, @unchecked Sendable {
    let session = AVCaptureSession()
    private let captureQueue = DispatchQueue(label: "airtake.capture", qos: .userInitiated)
    private let writerQueue = DispatchQueue(label: "airtake.writer", qos: .userInitiated)
    private let previewQueue = DispatchQueue(label: "airtake.preview", qos: .utility)
    private let lock = NSLock()
    private var device: AVCaptureDevice?
    private var encoder: VTCompressionSession?
    private var writer: AVAssetWriter?
    private var input: AVAssetWriterInput?
    private var spool: SpoolSession?
    private var settings = CaptureSettings()
    private var stats = CameraStats()
    private var running = false, stopping = false, previewBusy = false
    private var pendingFrames = 0
    private var origin: CMTime?, previousPTS: CMTime?
    private var clockOffset = 0.0, fpsWindowStart = 0.0, previewTime = 0.0
    private var fpsWindowFrames = 0
    private let context = CIContext(options: [.cacheIntermediates: false])
    var onError: ((String) -> Void)?
    var onPreview: ((String) -> Void)?
    var onFinished: (() -> Void)?
    var snapshot: CameraStats { lock.guarded { stats } }
    var isRecording: Bool { lock.guarded { running || stopping } }

    // Frame the shot before recording; no HEVC encoder or disk session is opened here.
    func prepare(settings: CaptureSettings) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            captureQueue.async { [self] in
                do {
                    guard !isRecording else { throw AirError.message("Дубль ещё завершается") }
                    try settings.validate(); self.settings = settings
                    try configureCamera()
                    if !session.isRunning { session.startRunning() }
                    continuation.resume()
                } catch { continuation.resume(throwing: error) }
            }
        }
    }
    func start(settings: CaptureSettings, spool: SpoolSession, clockOffset: Double) {
        captureQueue.async { [self] in
            guard !isRecording else { onError?("Предыдущий дубль ещё завершается"); return }
            do {
                try settings.validate(); self.settings = settings; self.spool = spool; self.clockOffset = clockOffset
                lock.guarded { stats = CameraStats(); pendingFrames = 0; stopping = false }
                origin = nil; previousPTS = nil; fpsWindowStart = hostTimeMs(); fpsWindowFrames = 0
                try configureCamera(); try configureEncoder()
                lock.guarded { running = true }
                if !session.isRunning { session.startRunning() }
            } catch { onError?(error.localizedDescription); teardownFailedStart() }
        }
    }
    private func configureCamera() throws {
        guard AVCaptureDevice.authorizationStatus(for: .video) == .authorized else { throw AirError.message("Разрешите доступ к камере") }
        guard let camera = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back) else { throw AirError.message("Основная задняя камера не найдена") }
        let candidates = camera.formats.filter { format in
            let d = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            return d.width == Int32(settings.width) && d.height == Int32(settings.height) &&
                format.videoSupportedFrameRateRanges.contains { $0.minFrameRate <= Double(settings.fps) && $0.maxFrameRate >= Double(settings.fps) }
        }
        guard let format = candidates.first(where: { CMFormatDescriptionGetMediaSubType($0.formatDescription) == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange }) ?? candidates.first
        else { throw AirError.message("Камера не предоставляет \(settings.width)×\(settings.height) / \(settings.fps) FPS. Режим не будет заменён на 60 FPS.") }
        session.beginConfiguration(); defer { session.commitConfiguration() }
        session.sessionPreset = .inputPriority
        session.inputs.forEach { session.removeInput($0) }; session.outputs.forEach { session.removeOutput($0) }
        let captureInput = try AVCaptureDeviceInput(device: camera)
        guard session.canAddInput(captureInput) else { throw AirError.message("Камера занята") }; session.addInput(captureInput)
        try camera.lockForConfiguration(); defer { camera.unlockForConfiguration() }
        camera.activeFormat = format
        camera.activeVideoMinFrameDuration = CMTime(value: 1, timescale: Int32(settings.fps))
        camera.activeVideoMaxFrameDuration = CMTime(value: 1, timescale: Int32(settings.fps))
        camera.videoZoomFactor = 1
        if camera.isFocusModeSupported(.continuousAutoFocus) { camera.focusMode = .continuousAutoFocus }
        if camera.isExposureModeSupported(.continuousAutoExposure) { camera.exposureMode = .continuousAutoExposure }
        if camera.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance) { camera.whiteBalanceMode = .continuousAutoWhiteBalance }
        camera.automaticallyAdjustsVideoHDREnabled = false
        if format.isVideoHDRSupported { camera.isVideoHDREnabled = false }
        let output = AVCaptureVideoDataOutput(); output.alwaysDiscardsLateVideoFrames = true
        let pixelFormat = output.availableVideoPixelFormatTypes.contains(kCVPixelFormatType_420YpCbCr8BiPlanarFullRange) ? kCVPixelFormatType_420YpCbCr8BiPlanarFullRange : kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: pixelFormat]
        output.setSampleBufferDelegate(self, queue: captureQueue)
        guard session.canAddOutput(output) else { throw AirError.message("Видеовыход недоступен") }; session.addOutput(output)
        if let connection = output.connection(with: .video) {
            if connection.isVideoStabilizationSupported { connection.preferredVideoStabilizationMode = .off }
            if connection.isVideoRotationAngleSupported(0) { connection.videoRotationAngle = 0 }
        }
        device = camera
    }
    private func configureEncoder() throws {
        let specification = [kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder as String: true] as CFDictionary
        let status = VTCompressionSessionCreate(allocator: kCFAllocatorDefault, width: Int32(settings.width), height: Int32(settings.height),
            codecType: kCMVideoCodecType_HEVC, encoderSpecification: specification, imageBufferAttributes: nil,
            compressedDataAllocator: nil, outputCallback: { ref, _, status, flags, sample in
                guard let ref else { return }
                let engine = Unmanaged<CameraEngine>.fromOpaque(ref).takeUnretainedValue()
                engine.encoded(status: status, flags: flags, sample: sample)
            }, refcon: Unmanaged.passUnretained(self).toOpaque(), compressionSessionOut: &encoder)
        guard status == noErr, let encoder else { throw AirError.message("Аппаратный HEVC-энкодер недоступен: \(status)") }
        let properties: [(CFString, CFTypeRef)] = [
            (kVTCompressionPropertyKey_RealTime, kCFBooleanTrue),
            (kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse),
            (kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_HEVC_Main_AutoLevel),
            (kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: settings.bitrateMbps * 1_000_000)),
            (kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: settings.fps)),
            (kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: settings.fps)),
            (kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, NSNumber(value: 1.0))
        ]
        for (key, value) in properties {
            let result = VTSessionSetProperty(encoder, key: key, value: value)
            guard result == noErr else { throw AirError.message("HEVC не принял параметр \(key): \(result)") }
        }
        let ready = VTCompressionSessionPrepareToEncodeFrames(encoder)
        guard ready == noErr else { throw AirError.message("Не удалось подготовить HEVC: \(ready)") }
    }
    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        updatePreview(pixelBuffer)
        guard lock.guarded({ running }), let encoder else { return }
        guard CVPixelBufferGetWidth(pixelBuffer) == settings.width, CVPixelBufferGetHeight(pixelBuffer) == settings.height else {
            fail("Камера вернула другое разрешение; масштабирование отключено"); return
        }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        guard pts.isValid, !pts.isIndefinite else { fail("Некорректная метка времени камеры"); return }
        if origin == nil { origin = pts; lock.guarded { stats.firstVideoTimeMs = pts.seconds * 1000 + clockOffset } }
        if let previous = previousPTS {
            let gap = pts.seconds - previous.seconds
            if gap <= 0 { fail("Камера выдала немонотонную метку времени"); return }
            if gap > 1.5 / Double(settings.fps) {
                lock.guarded { stats.timestampGaps += Int64(max(1, Int((gap * Double(settings.fps)).rounded()) - 1)) }
                if settings.stopOnDroppedFrame { fail("Пропуск кадра камеры: запись остановлена"); return }
            }
        }
        previousPTS = pts
        let relative = CMTimeSubtract(pts, origin!)
        let canEncode = lock.guarded { () -> Bool in
            if pendingFrames >= 16 { return false }
            pendingFrames += 1; stats.captured += 1; stats.durationSeconds = relative.seconds + 1 / Double(settings.fps); return true
        }
        guard canEncode else { fail("HEVC не успевает: очередь энкодера заполнена"); return }
        fpsWindowFrames += 1
        let now = hostTimeMs()
        if now - fpsWindowStart >= 1000 {
            lock.guarded { stats.fps = Double(fpsWindowFrames) * 1000 / (now - fpsWindowStart) }
            fpsWindowFrames = 0; fpsWindowStart = now
            if ProcessInfo.processInfo.thermalState == .critical { fail("Критический нагрев iPhone: запись остановлена"); return }
        }
        var flags = VTEncodeInfoFlags()
        let result = VTCompressionSessionEncodeFrame(encoder, imageBuffer: pixelBuffer, presentationTimeStamp: relative,
            duration: CMTime(value: 1, timescale: Int32(settings.fps)), frameProperties: nil, sourceFrameRefcon: nil, infoFlagsOut: &flags)
        if result != noErr { lock.guarded { pendingFrames = max(0, pendingFrames - 1); stats.outputDrops += 1 }; fail("Ошибка HEVC: \(result)"); return }
    }
    private func updatePreview(_ pixelBuffer: CVPixelBuffer) {
        let now = hostTimeMs()
        if now - previewTime >= 500, lock.guarded({ if previewBusy { return false }; previewBusy = true; return true }) {
            previewTime = now
            previewQueue.async { [self] in
                defer { lock.guarded { previewBusy = false } }
                let image = CIImage(cvPixelBuffer: pixelBuffer)
                let scale = 480.0 / image.extent.width
                let resized = image.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
                if let cgImage = context.createCGImage(resized, from: resized.extent), let data = UIImage(cgImage: cgImage).jpegData(compressionQuality: 0.55) { onPreview?(data.base64EncodedString()) }
            }
        }
    }
    func captureOutput(_ output: AVCaptureOutput, didDrop sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard lock.guarded({ running }) else { return }
        lock.guarded { stats.outputDrops += 1 }
        if settings.stopOnDroppedFrame { fail("AVFoundation сообщил потерю кадра") }
    }
    private func encoded(status: OSStatus, flags: VTEncodeInfoFlags, sample: CMSampleBuffer?) {
        lock.guarded { pendingFrames = max(0, pendingFrames - 1) }
        guard status == noErr, !flags.contains(.frameDropped), let sample, CMSampleBufferDataIsReady(sample) else {
            lock.guarded { stats.outputDrops += 1 }; fail("HEVC потерял кадр или вернул ошибку \(status)"); return
        }
        writerQueue.async { [self] in
            do {
                if writer == nil {
                    let newWriter = AVAssetWriter(contentType: .mpeg4Movie)
                    newWriter.outputFileTypeProfile = .mpeg4AppleHLS
                    newWriter.preferredOutputSegmentInterval = CMTime(seconds: 1, preferredTimescale: 600)
                    newWriter.initialSegmentStartTime = .zero
                    newWriter.delegate = self
                    let newInput = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: CMSampleBufferGetFormatDescription(sample))
                    newInput.expectsMediaDataInRealTime = true
                    guard newWriter.canAdd(newInput) else { throw AirError.message("Нельзя добавить HEVC-поток в MP4") }
                    newWriter.add(newInput)
                    guard newWriter.startWriting() else { throw newWriter.error ?? AirError.message("MP4 writer не запустился") }
                    newWriter.startSession(atSourceTime: .zero); writer = newWriter; input = newInput
                }
                guard let input, input.isReadyForMoreMediaData, input.append(sample) else { throw writer?.error ?? AirError.message("MP4 writer не успевает; кадр не записан") }
                lock.guarded { stats.encoded += 1 }
            } catch { lock.guarded { stats.outputDrops += 1 }; fail(error.localizedDescription) }
        }
    }
    func assetWriter(_ writer: AVAssetWriter, didOutputSegmentData segmentData: Data, segmentType: AVAssetSegmentType, segmentReport: AVAssetSegmentReport?) {
        do {
            guard let spool else { throw AirError.message("Буфер дубля недоступен") }
            if try !spool.append(segmentData, stats: snapshot) { fail("Wi-Fi не успевает: буфер заполнен. Данные сохранены, запись остановлена.") }
        } catch { fail("Ошибка буфера: " + error.localizedDescription) }
    }
    private func fail(_ message: String) { onError?(message); stop(reason: message) }
    func stop(reason: String = "user") {
        let shouldStop = lock.guarded { () -> Bool in if !running || stopping { return false }; stopping = true; return true }
        guard shouldStop else { return }
        captureQueue.async { [self] in
            lock.guarded { running = false }
            if let encoder { VTCompressionSessionCompleteFrames(encoder, untilPresentationTimeStamp: .invalid); VTCompressionSessionInvalidate(encoder); self.encoder = nil }
            writerQueue.async { [self] in
                guard let writer, let input else {
                    lock.guarded { stopping = false }; onError?("Не получены видеофрагменты"); onFinished?(); return
                }
                input.markAsFinished()
                writer.finishWriting { [self] in
                    do {
                        guard writer.status == .completed else { throw writer.error ?? AirError.message("Не удалось завершить MP4") }
                        try spool?.finish(stats: snapshot, reason: reason)
                    } catch { onError?(error.localizedDescription) }
                    self.writer = nil; self.input = nil
                    lock.guarded { stopping = false }; onFinished?()
                }
            }
        }
    }
    private func teardownFailedStart() {
        if let encoder { VTCompressionSessionInvalidate(encoder); self.encoder = nil }
        lock.guarded { running = false; stopping = false }; onFinished?()
    }
    func pausePreview() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            captureQueue.async { [self] in
                if !isRecording && session.isRunning { session.stopRunning() }
                continuation.resume()
            }
        }
    }
    func setLocks(focus: Bool, exposure: Bool, whiteBalance: Bool) {
        captureQueue.async { [self] in
            guard let device else { return }
            do {
                try device.lockForConfiguration(); defer { device.unlockForConfiguration() }
                let fm: AVCaptureDevice.FocusMode = focus ? .locked : .continuousAutoFocus
                let em: AVCaptureDevice.ExposureMode = exposure ? .locked : .continuousAutoExposure
                let wm: AVCaptureDevice.WhiteBalanceMode = whiteBalance ? .locked : .continuousAutoWhiteBalance
                if device.isFocusModeSupported(fm) { device.focusMode = fm }
                if device.isExposureModeSupported(em) { device.exposureMode = em }
                if device.isWhiteBalanceModeSupported(wm) { device.whiteBalanceMode = wm }
            } catch { onError?(error.localizedDescription) }
        }
    }
}
