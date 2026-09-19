package engine

import (
	"context"
	"errors"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

type Part struct {
	File            string  `json:"file"`
	Frames          int64   `json:"frames"`
	Duration        float64 `json:"duration"`
	ApproxMicOffset float64 `json:"approxMicOffset"`
	ClockEstimated  bool    `json:"clockEstimated"`
	EndReason       string  `json:"endReason"`
	Format          string  `json:"format"`
}
type Exported struct {
	Source        string  `json:"source"`
	File          string  `json:"file"`
	Sync          string  `json:"sync"`
	OffsetSeconds float64 `json:"offsetSeconds"`
	Tempo         float64 `json:"tempo"`
	Confidence    float64 `json:"confidence"`
}
type Manifest struct {
	Version        int        `json:"version"`
	Started        time.Time  `json:"started"`
	Finished       *time.Time `json:"finished,omitempty"`
	Folder         string     `json:"folder"`
	Config         Config     `json:"config"`
	Parts          []*Part    `json:"parts"`
	MicrophoneFile string     `json:"microphoneFile,omitempty"`
	MicDuration    float64    `json:"micDuration"`
	Exports        []Exported `json:"exports"`
	Warnings       []string   `json:"warnings"`
	State          string     `json:"state"`
}
type Status struct {
	Stage        string   `json:"stage"`
	Message      string   `json:"message"`
	Folder       string   `json:"folder"`
	Format       string   `json:"format"`
	Frames       int64    `json:"frames"`
	MediaSeconds float64  `json:"mediaSeconds"`
	MeasuredFPS  float64  `json:"measuredFps"`
	Elapsed      float64  `json:"elapsed"`
	MicSeconds   float64  `json:"micSeconds"`
	WrittenBytes int64    `json:"writtenBytes"`
	WriteMbps    float64  `json:"writeMbps"`
	FreeBytes    uint64   `json:"freeBytes"`
	Disconnects  int      `json:"disconnects"`
	Busy         bool     `json:"busy"`
	Logs         []string `json:"logs"`
	Error        string   `json:"error,omitempty"`
}
type Recorder struct {
	mu       sync.Mutex
	tools    Tools
	status   Status
	manifest *Manifest
}

func NewRecorder(t Tools) *Recorder {
	return &Recorder{tools: t, status: Status{Stage: "idle", Message: "Готов к подключению", Logs: []string{}}}
}
func (r *Recorder) SetTools(t Tools) { r.mu.Lock(); r.tools = t; r.mu.Unlock() }
func (r *Recorder) Snapshot() Status {
	r.mu.Lock()
	defer r.mu.Unlock()
	s := r.status
	s.Logs = append([]string{}, s.Logs...)
	return s
}
func (r *Recorder) Log(s string) { r.mu.Lock(); defer r.mu.Unlock(); r.logLocked(s) }
func (r *Recorder) logLocked(s string) {
	s = Redact(s)
	if len(s) > 3000 {
		s = s[:3000]
	}
	r.status.Logs = append(r.status.Logs, time.Now().Format("15:04:05")+"  "+s)
	if len(r.status.Logs) > 180 {
		r.status.Logs = r.status.Logs[len(r.status.Logs)-180:]
	}
}
func (r *Recorder) set(stage, msg string) {
	r.mu.Lock()
	r.status.Stage = stage
	r.status.Message = msg
	r.mu.Unlock()
}
func (r *Recorder) SaveManifest() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.manifest == nil {
		return nil
	}
	return AtomicJSON(filepath.Join(r.manifest.Folder, "take.json"), r.manifest)
}
func (r *Recorder) fail(err error) {
	r.mu.Lock()
	r.status.Error = err.Error()
	r.status.Stage = "error"
	r.status.Message = err.Error()
	r.status.Busy = false
	r.logLocked(err.Error())
	r.mu.Unlock()
}
func (r *Recorder) ExportState(busy bool, msg string) {
	r.mu.Lock()
	r.status.Busy = busy
	if busy {
		r.status.Stage = "exporting"
	} else {
		r.status.Stage = "done"
	}
	r.status.Message = msg
	r.mu.Unlock()
}

var videoRE = regexp.MustCompile(`Video:\s*([^,]+).*?\b(\d{3,5})x(\d{3,5})\b.*?(\d+(?:\.\d+)?) fps`)

func ParseVideo(s string) (string, int, int, float64) {
	m := videoRE.FindStringSubmatch(s)
	if m == nil {
		return "", 0, 0, 0
	}
	w, _ := strconv.Atoi(m[2])
	h, _ := strconv.Atoi(m[3])
	f, _ := strconv.ParseFloat(m[4], 64)
	return fmt.Sprintf("%s · %s×%s · %s FPS", strings.TrimSpace(m[1]), m[2], m[3], m[4]), w, h, f
}

func (r *Recorder) Record(parent context.Context, c Config) (folder string, retErr error) {
	if e := c.Validate(); e != nil {
		return "", e
	}
	r.mu.Lock()
	if r.status.Busy {
		r.mu.Unlock()
		return "", errors.New("Запись уже запущена")
	}
	t := r.tools
	r.status = Status{Stage: "preparing", Message: "Проверка движка и диска", Busy: true, Logs: []string{}}
	r.mu.Unlock()
	defer func() {
		if retErr != nil {
			r.fail(retErr)
		}
		r.mu.Lock()
		r.status.Busy = false
		r.mu.Unlock()
	}()
	checkCtx, cc := context.WithTimeout(parent, 15*time.Second)
	e := t.Check(checkCtx)
	cc()
	if e != nil {
		return "", e
	}
	if e = os.MkdirAll(c.OutputDir, 0700); e != nil {
		return "", e
	}
	free, e := FreeDisk(c.OutputDir)
	if e != nil {
		return "", e
	}
	if free < uint64(c.ReserveGB*1e9) {
		return "", errors.New("На диске меньше заданного резерва")
	}
	probe, e := os.CreateTemp(c.OutputDir, ".airtake-write-test-")
	if e != nil {
		return "", fmt.Errorf("Папка недоступна для записи: %w", e)
	}
	_ = probe.Close()
	_ = os.Remove(probe.Name())
	folder, e = os.MkdirTemp(c.OutputDir, "Take_"+time.Now().Format("2006-01-02_15-04-05")+"_")
	if e != nil {
		return "", e
	}
	logFile, e := os.OpenFile(filepath.Join(folder, "capture.log"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if e != nil {
		return folder, e
	}
	defer logFile.Close()
	var logMu sync.Mutex
	logLine := func(s string) {
		s = Redact(s)
		logMu.Lock()
		_, _ = fmt.Fprintln(logFile, time.Now().Format(time.RFC3339Nano), s)
		logMu.Unlock()
		r.Log(s)
	}
	redacted := c
	redacted.Passphrase = ""
	m := &Manifest{Version: 1, Started: time.Now(), Folder: folder, Config: redacted, Parts: []*Part{}, Exports: []Exported{}, Warnings: []string{}, State: "recording"}
	r.mu.Lock()
	r.manifest = m
	r.status.Folder = folder
	r.status.FreeBytes = free
	r.mu.Unlock()
	if e = r.SaveManifest(); e != nil {
		return folder, e
	}
	ctx, cancel := context.WithCancel(parent)
	defer cancel()
	start := time.Now()
	var fatalMu sync.Mutex
	var fatal error
	abort := func(e error) {
		fatalMu.Lock()
		if fatal == nil {
			fatal = e
		}
		fatalMu.Unlock()
		cancel()
	}
	var mic *Process
	var micZero float64
	var micZeroKnown bool
	if c.Microphone != "" {
		r.mu.Lock()
		m.MicrophoneFile = "microphone.wav"
		r.mu.Unlock()
		ready := make(chan struct{})
		var once sync.Once
		mic, e = StartProcess(t.FFmpeg, MicrophoneArgs(c.Microphone, m.MicrophoneFile), folder, logLine, func(s string) {
			if strings.HasPrefix(s, "out_time_us=") {
				us, _ := strconv.ParseFloat(strings.TrimPrefix(s, "out_time_us="), 64)
				if us > 0 {
					r.mu.Lock()
					r.status.MicSeconds = us / 1e6
					if !micZeroKnown {
						micZero = time.Since(start).Seconds() - us/1e6
						micZeroKnown = true
					}
					r.mu.Unlock()
					once.Do(func() { close(ready) })
				}
			}
		})
		if e != nil {
			return folder, e
		}
		select {
		case <-ready:
		case <-mic.done:
			mic.Stop()
			return folder, errors.New("Микрофон не открылся. Проверьте выбранное устройство и журнал")
		case <-time.After(12 * time.Second):
			mic.Stop()
			return folder, errors.New("Микрофон не начал передавать звук за 12 секунд")
		case <-ctx.Done():
			mic.Stop()
			return folder, ctx.Err()
		}
		go func() {
			<-mic.done
			if ctx.Err() == nil {
				abort(errors.New("Запись микрофона прервалась; видеозапись остановлена. Исходники сохранены"))
			}
		}()
	}
	var preview *Process
	previewPort := 0
	if c.Preview && regular(t.FFplay) {
		conn, er := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 0})
		if er == nil {
			previewPort = conn.LocalAddr().(*net.UDPAddr).Port
			_ = conn.Close()
			preview, er = StartProcess(t.FFplay, []string{"-hide_banner", "-loglevel", "error", "-an", "-framedrop", "-sync", "ext", "-window_title", "AirTake Preview", "-x", "960", "-y", "540", "-i", fmt.Sprintf("udp://127.0.0.1:%d?fifo_size=131072&overrun_nonfatal=1", previewPort)}, folder, func(s string) { logLine("preview: " + s) }, nil)
			if er != nil {
				previewPort = 0
				logLine("Предпросмотр недоступен: " + er.Error())
			}
		}
	}
	if preview != nil {
		defer func() { _ = preview.cmd.Process.Kill(); _ = preview.Wait() }()
	}
	watchdogDone := make(chan struct{})
	go func() {
		defer close(watchdogDone)
		ticker := time.NewTicker(time.Second)
		defer ticker.Stop()
		var lastBytes int64
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				f, er := FreeDisk(folder)
				if er != nil {
					abort(fmt.Errorf("Проверка диска: %w", er))
					return
				}
				if f < uint64(c.ReserveGB*1e9) {
					abort(errors.New("Запись остановлена: достигнут резерв свободного места"))
					return
				}
				if c.MaxMinutes > 0 && time.Since(start) > time.Duration(c.MaxMinutes)*time.Minute {
					logLine("Достигнут заданный лимит времени")
					cancel()
					return
				}
				files, _ := os.ReadDir(folder)
				var total int64
				for _, f := range files {
					if i, e := f.Info(); e == nil && !i.IsDir() {
						total += i.Size()
					}
				}
				r.mu.Lock()
				r.status.Elapsed = time.Since(start).Seconds()
				r.status.FreeBytes = f
				r.status.WrittenBytes = total
				r.status.WriteMbps = float64(total-lastBytes) * 8 / 1e6
				r.mu.Unlock()
				lastBytes = total
				if er = r.SaveManifest(); er != nil {
					abort(fmt.Errorf("Не удалось сохранить описание дубля: %w", er))
					return
				}
			}
		}
	}()
	quickFailures := 0
	for seq := 1; ctx.Err() == nil; seq++ {
		part := &Part{File: fmt.Sprintf("source-%04d.mkv", seq), EndReason: "in_progress"}
		r.mu.Lock()
		m.Parts = append(m.Parts, part)
		r.status.Stage = "listening"
		r.status.Message = "Ожидание Moblin — включите трансляцию на телефоне"
		r.status.Format = ""
		r.mu.Unlock()
		logLine(fmt.Sprintf("SRT listener %s:%d; файл %s; passthrough", c.Address, c.Port, part.File))
		launch := time.Now()
		formatWarned := false
		proc, er := StartProcess(t.FFmpeg, ReceiveArgs(c, part.File, previewPort), folder, func(s string) {
			logLine(s)
			if format, w, h, f := ParseVideo(s); format != "" {
				r.mu.Lock()
				part.Format = format
				r.status.Format = format
				if !formatWarned && (w != c.Width || h != c.Height || f < float64(c.FPS)*0.98) {
					formatWarned = true
					warning := "Источник не соответствует профилю: " + format + ". Кадры не дорисовываются."
					m.Warnings = append(m.Warnings, warning)
					r.logLocked(warning)
				}
				r.mu.Unlock()
			}
		}, func(s string) {
			k, v, ok := strings.Cut(s, "=")
			if !ok {
				return
			}
			r.mu.Lock()
			defer r.mu.Unlock()
			switch k {
			case "frame":
				n, _ := strconv.ParseInt(strings.TrimSpace(v), 10, 64)
				part.Frames = n
			case "out_time_us":
				us, _ := strconv.ParseFloat(v, 64)
				if us > 0 {
					part.Duration = us / 1e6
					if !part.ClockEstimated {
						part.ApproxMicOffset = time.Since(start).Seconds() - part.Duration - float64(c.LatencyMS)/1000 - micZero
						part.ClockEstimated = true
					}
					if ctx.Err() == nil {
						r.status.Stage = "recording"
						r.status.Message = "Запись исходного потока на диск"
					}
				}
			case "progress":
				var frames int64
				var dur float64
				for _, p := range m.Parts {
					frames += p.Frames
					dur += p.Duration
				}
				r.status.Frames = frames
				r.status.MediaSeconds = dur
				if dur > 0 {
					r.status.MeasuredFPS = float64(frames) / dur
				}
			}
		})
		if er != nil {
			abort(er)
			break
		}
		select {
		case <-ctx.Done():
			r.set("stopping", "Сохранение файлов — пока не отключайте Moblin")
			// A user stop drains the receiver's live latency before closing. Fatal
			// disk/audio errors must stop immediately. This cannot recover packets
			// from a sender that already closed abruptly.
			fatalMu.Lock()
			hasFatal := fatal != nil
			fatalMu.Unlock()
			r.mu.Lock()
			hasFrames := part.Frames > 0
			r.mu.Unlock()
			if !hasFatal && hasFrames {
				select {
				case <-proc.done:
				case <-time.After(time.Duration(c.LatencyMS+350) * time.Millisecond):
				}
			}
			proc.Stop()
		case <-proc.done:
		}
		perr := proc.Wait()
		// Read the finalized container rather than treating FFmpeg's live
		// progress timestamp (which may precede the last packet) as duration.
		probeCtx, probeCancel := context.WithTimeout(context.Background(), 10*time.Second)
		info, probeErr := t.Probe(probeCtx, filepath.Join(folder, part.File))
		probeCancel()
		r.mu.Lock()
		if probeErr == nil && info.Duration > 0 {
			part.Duration = info.Duration
		}
		var frames int64
		var duration float64
		for _, p := range m.Parts {
			frames += p.Frames
			duration += p.Duration
		}
		r.status.Frames, r.status.MediaSeconds = frames, duration
		if duration > 0 {
			r.status.MeasuredFPS = float64(frames) / duration
		}
		part.EndReason = "stopped"
		hasVideo := part.Frames > 0
		r.mu.Unlock()
		if ctx.Err() != nil {
			if perr != nil {
				logLine("Остановка приёма: " + perr.Error())
			}
			break
		}
		if hasVideo {
			r.mu.Lock()
			part.EndReason = "connection_lost"
			r.status.Disconnects++
			warning := "Обрыв связи: недополученный интервал восстановить нельзя. Продолжение — в новом файле."
			m.Warnings = append(m.Warnings, warning)
			r.logLocked(warning)
			r.mu.Unlock()
			quickFailures = 0
		} else {
			r.mu.Lock()
			part.EndReason = "no_video"
			r.mu.Unlock()
			if time.Since(launch) < 2*time.Second {
				quickFailures++
			} else {
				quickFailures = 0
			}
		}
		if quickFailures >= 3 {
			abort(errors.New("Приёмник трижды завершился до подключения. Проверьте порт, сетевой адрес и capture.log"))
			break
		}
		if !c.AutoReconnect {
			cancel()
			break
		}
		r.set("reconnecting", "Ожидание повторного подключения Moblin")
		select {
		case <-ctx.Done():
		case <-time.After(time.Second):
		}
	}
	cancel()
	<-watchdogDone
	if mic != nil {
		mic.Stop()
	}
	files, _ := os.ReadDir(folder)
	var finalBytes int64
	for _, f := range files {
		if info, err := f.Info(); err == nil && !info.IsDir() {
			finalBytes += info.Size()
		}
	}
	now := time.Now()
	r.mu.Lock()
	r.status.WrittenBytes = finalBytes
	r.status.WriteMbps = 0
	m.Finished = &now
	m.MicDuration = r.status.MicSeconds
	m.State = "recorded"
	r.status.Elapsed = time.Since(start).Seconds()
	r.mu.Unlock()
	fatalMu.Lock()
	fe := fatal
	fatalMu.Unlock()
	if fe != nil {
		r.mu.Lock()
		m.State = "interrupted"
		m.Warnings = append(m.Warnings, fe.Error())
		r.mu.Unlock()
	}
	if e = r.SaveManifest(); e != nil {
		return folder, e
	}
	if fe != nil {
		return folder, fe
	}
	r.set("done", "Исходники сохранены")
	return folder, nil
}
