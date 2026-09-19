package engine

import (
	"bufio"
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"sync"
	"time"
)

type Tools struct {
	FFmpeg  string `json:"ffmpeg"`
	FFprobe string `json:"ffprobe"`
	FFplay  string `json:"ffplay"`
}

func FindTools(base, cache string) Tools {
	ext := ""
	if runtime.GOOS == "windows" {
		ext = ".exe"
	}
	dirs := []string{filepath.Join(base, "tools"), filepath.Join(base, "ffmpeg"), base, filepath.Join(cache, "tools")}
	for _, d := range dirs {
		a := filepath.Join(d, "ffmpeg"+ext)
		b := filepath.Join(d, "ffprobe"+ext)
		if regular(a) && regular(b) {
			return Tools{a, b, filepath.Join(d, "ffplay"+ext)}
		}
	}
	a, _ := exec.LookPath("ffmpeg" + ext)
	b, _ := exec.LookPath("ffprobe" + ext)
	p, _ := exec.LookPath("ffplay" + ext)
	return Tools{a, b, p}
}
func regular(p string) bool { s, e := os.Stat(p); return e == nil && s.Mode().IsRegular() }
func (t Tools) Check(ctx context.Context) error {
	if t.FFmpeg == "" || t.FFprobe == "" {
		return errors.New("FFmpeg не найден. Нажмите «Подготовить движок» для однократной загрузки")
	}
	b, e := Run(ctx, t.FFmpeg, []string{"-hide_banner", "-protocols"}, "")
	if e != nil {
		return e
	}
	if !regexp.MustCompile(`(?m)^\s+srt\s*$`).Match(b) {
		return errors.New("Эта сборка FFmpeg не поддерживает SRT")
	}
	return nil
}

var passRE = regexp.MustCompile(`(?i)(passphrase=)[^&\s"']+`)

func Redact(s string) string { return passRE.ReplaceAllString(s, "${1}***") }

type Process struct {
	cmd      *exec.Cmd
	input    io.WriteCloser
	done     chan struct{}
	err      error
	mu       sync.Mutex
	stopOnce sync.Once
}

func StartProcess(bin string, args []string, dir string, line func(string), progress func(string)) (*Process, error) {
	cmd := exec.Command(bin, args...)
	configureProcess(cmd)
	cmd.Dir = dir
	in, e := cmd.StdinPipe()
	if e != nil {
		return nil, e
	}
	stderr, e := cmd.StderrPipe()
	if e != nil {
		return nil, e
	}
	stdout, e := cmd.StdoutPipe()
	if e != nil {
		return nil, e
	}
	if e = cmd.Start(); e != nil {
		return nil, e
	}
	p := &Process{cmd: cmd, input: in, done: make(chan struct{})}
	var wg sync.WaitGroup
	wg.Add(2)
	read := func(r io.Reader, fn func(string)) {
		defer wg.Done()
		s := bufio.NewScanner(r)
		s.Buffer(make([]byte, 4096), 1024*1024)
		for s.Scan() {
			if fn != nil {
				fn(s.Text())
			}
		}
	}
	go read(stderr, line)
	go read(stdout, progress)
	go func() {
		wg.Wait()
		e := cmd.Wait()
		p.mu.Lock()
		p.err = e
		p.mu.Unlock()
		close(p.done)
	}()
	return p, nil
}
func (p *Process) Wait() error { <-p.done; p.mu.Lock(); defer p.mu.Unlock(); return p.err }
func (p *Process) Stop() {
	if p == nil {
		return
	}
	p.stopOnce.Do(func() {
		select {
		case <-p.done:
			return
		default:
		}
		_, _ = io.WriteString(p.input, "q\n")
		select {
		case <-p.done:
		case <-time.After(6 * time.Second):
			_ = p.cmd.Process.Kill()
			<-p.done
		}
		_ = p.input.Close()
	})
}
func Run(ctx context.Context, bin string, args []string, dir string) ([]byte, error) {
	c := exec.CommandContext(ctx, bin, args...)
	configureProcess(c)
	c.Dir = dir
	b, e := c.CombinedOutput()
	if e != nil {
		return b, fmt.Errorf("%s: %w\n%s", filepath.Base(bin), e, Redact(tail(string(b), 4000)))
	}
	return b, nil
}
func tail(s string, n int) string {
	if len(s) > n {
		return s[len(s)-n:]
	}
	return s
}
func BinaryOutput(ctx context.Context, bin string, args []string) ([]byte, error) {
	c := exec.CommandContext(ctx, bin, args...)
	configureProcess(c)
	var er bytes.Buffer
	c.Stderr = &er
	r, e := c.StdoutPipe()
	if e != nil {
		return nil, e
	}
	if e = c.Start(); e != nil {
		return nil, e
	}
	b, re := io.ReadAll(io.LimitReader(r, 16*1024*1024+1))
	if re != nil || len(b) > 16*1024*1024 {
		_ = c.Process.Kill()
		_ = c.Wait()
		return nil, errors.New("Слишком большой фрагмент для анализа звука")
	}
	if e = c.Wait(); e != nil {
		return nil, fmt.Errorf("Анализ звука: %w: %s", e, tail(er.String(), 2000))
	}
	return b, nil
}

type Device struct {
	Name string `json:"name"`
	ID   string `json:"id"`
}

var deviceRE = regexp.MustCompile(`"(.*)" \(audio\)`)
var altRE = regexp.MustCompile(`Alternative name "(.*)"`)

func ParseDevices(s string) []Device {
	ds := []Device{}
	var last int = -1
	for _, l := range strings.Split(s, "\n") {
		if m := deviceRE.FindStringSubmatch(l); m != nil {
			ds = append(ds, Device{m[1], m[1]})
			last = len(ds) - 1
			continue
		}
		if strings.Contains(l, "(video)") {
			last = -1
		}
		if m := altRE.FindStringSubmatch(l); m != nil && last >= 0 {
			ds[last].ID = m[1]
			last = -1
		}
	}
	return ds
}
func (t Tools) Devices(ctx context.Context) ([]Device, error) {
	if runtime.GOOS != "windows" {
		return []Device{}, nil
	}
	b, _ := Run(ctx, t.FFmpeg, []string{"-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"}, "")
	ds := ParseDevices(string(b))
	if len(ds) == 0 {
		return ds, fmt.Errorf("Микрофоны не найдены. Проверьте разрешение Windows на доступ к микрофону. %s", Redact(tail(string(b), 1000)))
	}
	return ds, nil
}
func MicrophoneArgs(device, path string) []string {
	return []string{"-hide_banner", "-y", "-nostats", "-stats_period", "0.5", "-progress", "pipe:1", "-thread_queue_size", "2048", "-f", "dshow", "-audio_buffer_size", "100", "-i", "audio=" + device, "-vn", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s24le", "-rf64", "auto", path}
}
func ReceiveArgs(c Config, name string, previewPort int) []string {
	a := []string{"-hide_banner", "-y", "-nostats", "-stats_period", "0.5", "-progress", "pipe:1", "-thread_queue_size", "4096", "-analyzeduration", "1000000", "-probesize", "4000000", "-fflags", "+genpts", "-i", c.ReceiveURL(), "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy"}
	if previewPort > 0 {
		target := fmt.Sprintf("[f=matroska:onfail=abort:cluster_time_limit=1000]%s|[f=mpegts:onfail=ignore:use_fifo=1:fifo_options=drop_pkts_on_overflow=1]udp://127.0.0.1:%d?pkt_size=1316", name, previewPort)
		return append(a, "-f", "tee", target)
	}
	return append(a, "-cluster_time_limit", "1000", "-f", "matroska", name)
}
