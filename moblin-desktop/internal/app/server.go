package app

import (
	"archive/zip"
	"context"
	"crypto/rand"
	"crypto/subtle"
	"embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"time"

	"github.com/natural0101/AirTake/internal/engine"
	"github.com/natural0101/AirTake/internal/qr"
)

//go:embed web/*
var assets embed.FS

const Version = "0.1.0"

type instance struct {
	URL   string `json:"url"`
	Token string `json:"token"`
	PID   int    `json:"pid"`
}
type App struct {
	mu                         sync.Mutex
	cfg                        engine.Config
	tools                      engine.Tools
	recorder                   *engine.Recorder
	cache, base, token, origin string
	cancel                     context.CancelFunc
	working                    bool
	lastFolder                 string
	lastView                   time.Time
	quit                       chan struct{}
	quitOnce                   sync.Once
	server                     *http.Server
}

func dataDir() string {
	if d := os.Getenv("AIRTAKE_DATA_DIR"); d != "" {
		return d
	}
	d, e := os.UserConfigDir()
	if e != nil {
		d = os.TempDir()
	}
	return filepath.Join(d, "AirTake")
}
func Run() error {
	cache := dataDir()
	if e := os.MkdirAll(cache, 0700); e != nil {
		return e
	}
	// A second .exe opens the running instance rather than stealing the SRT port.
	if b, e := os.ReadFile(filepath.Join(cache, "instance.json")); e == nil {
		var old instance
		if json.Unmarshal(b, &old) == nil && engine.AllowedInstanceURL(old.URL) && len(old.Token) == 48 {
			req, _ := http.NewRequest("GET", old.URL+"api/ping", nil)
			req.Header.Set("X-AirTake-Token", old.Token)
			client := &http.Client{Timeout: time.Second}
			if res, e := client.Do(req); e == nil {
				b, _ := io.ReadAll(io.LimitReader(res.Body, 100))
				res.Body.Close()
				if res.StatusCode == 200 && strings.Contains(string(b), "AirTake") {
					return openBrowser(old.URL+"#"+old.Token, cache)
				}
			}
		}
	}
	ln, e := net.Listen("tcp4", "127.0.0.1:0")
	if e != nil {
		return e
	}
	exe, _ := os.Executable()
	base := filepath.Dir(exe)
	b := make([]byte, 24)
	if _, e = rand.Read(b); e != nil {
		ln.Close()
		return e
	}
	a := &App{cache: cache, base: base, token: hex.EncodeToString(b), cfg: engine.LoadConfig(filepath.Join(cache, "settings.json")), quit: make(chan struct{}), lastView: time.Now()}
	a.tools = engine.FindTools(base, cache)
	a.recorder = engine.NewRecorder(a.tools)
	a.origin = "http://" + ln.Addr().String()
	if b, e := os.ReadFile(filepath.Join(cache, "last-take.txt")); e == nil {
		a.lastFolder = strings.TrimSpace(string(b))
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/api/", a.api)
	sub, _ := fs.Sub(assets, "web")
	mux.Handle("/", http.FileServer(http.FS(sub)))
	a.server = &http.Server{Handler: a.headers(mux), ReadHeaderTimeout: 5 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 8192}
	inst := instance{URL: a.origin + "/", Token: a.token, PID: os.Getpid()}
	if e = engine.AtomicJSON(filepath.Join(cache, "instance.json"), inst); e != nil {
		ln.Close()
		return e
	}
	defer os.Remove(filepath.Join(cache, "instance.json"))
	go func() {
		if er := a.server.Serve(ln); er != nil && er != http.ErrServerClosed {
			a.recorder.Log(er.Error())
			a.quitOnce.Do(func() { close(a.quit) })
		}
	}()
	if os.Getenv("AIRTAKE_NO_BROWSER") == "" {
		if e = openBrowser(a.origin+"/#"+a.token, cache); e != nil {
			a.server.Close()
			return e
		}
	}
	fmt.Println("AirTake is running on", a.origin)
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-a.quit:
			a.mu.Lock()
			if a.cancel != nil {
				a.cancel()
			}
			a.mu.Unlock()
			ctx, c := context.WithTimeout(context.Background(), 10*time.Second)
			defer c()
			return a.server.Shutdown(ctx)
		case <-ticker.C:
			a.mu.Lock()
			idle := !a.working && time.Since(a.lastView) > 90*time.Second
			a.mu.Unlock()
			if idle && os.Getenv("AIRTAKE_NO_BROWSER") == "" {
				a.quitOnce.Do(func() { close(a.quit) })
			}
		}
	}
}
func (a *App) headers(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if "http://"+r.Host != a.origin {
			http.Error(w, "Invalid host", http.StatusForbidden)
			return
		}
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
		next.ServeHTTP(w, r)
	})
}
func jsonReply(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
func errorReply(w http.ResponseWriter, e error) {
	jsonReply(w, 400, map[string]string{"error": e.Error()})
}
func readJSON(w http.ResponseWriter, r *http.Request, v any) error {
	r.Body = http.MaxBytesReader(w, r.Body, 128*1024)
	d := json.NewDecoder(r.Body)
	d.DisallowUnknownFields()
	return d.Decode(v)
}
func (a *App) allowed(w http.ResponseWriter, r *http.Request) bool {
	token := r.Header.Get("X-AirTake-Token")
	if subtle.ConstantTimeCompare([]byte(token), []byte(a.token)) != 1 {
		http.Error(w, "Unauthorized", 401)
		return false
	}
	if origin := r.Header.Get("Origin"); origin != "" && origin != a.origin {
		http.Error(w, "Invalid origin", 403)
		return false
	}
	return true
}
func (a *App) jobStart() (context.Context, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.working {
		return nil, errors.New("Дождитесь окончания текущей операции")
	}
	ctx, c := context.WithCancel(context.Background())
	a.cancel = c
	a.working = true
	return ctx, nil
}
func (a *App) jobDone() { a.mu.Lock(); a.working = false; a.cancel = nil; a.mu.Unlock() }
func (a *App) state() map[string]any {
	a.mu.Lock()
	c := a.cfg
	t := a.tools
	busy := a.working
	last := a.lastFolder
	a.lastView = time.Now()
	a.mu.Unlock()
	s := a.recorder.Snapshot()
	s.Busy = busy
	return map[string]any{"version": Version, "config": c, "toolsReady": t.FFmpeg != "" && t.FFprobe != "", "status": s, "addresses": engine.Addresses(), "srtUrl": c.SendURL(), "moblinUrl": c.MoblinURL(), "lastFolder": last, "gbPerHour": float64(c.BitrateMbps) * .45, "platform": runtime.GOOS}
}
func (a *App) api(w http.ResponseWriter, r *http.Request) {
	if !a.allowed(w, r) {
		return
	}
	path := strings.TrimPrefix(r.URL.Path, "/api/")
	if r.Method == "GET" {
		switch path {
		case "ping":
			jsonReply(w, 200, map[string]string{"app": "AirTake", "version": Version})
		case "state":
			jsonReply(w, 200, a.state())
		case "devices":
			a.mu.Lock()
			t := a.tools
			busy := a.working
			a.mu.Unlock()
			if busy {
				errorReply(w, errors.New("Обновляйте список микрофонов до записи"))
				return
			}
			ctx, c := context.WithTimeout(r.Context(), 12*time.Second)
			defer c()
			ds, e := t.Devices(ctx)
			if e != nil {
				errorReply(w, e)
				return
			}
			jsonReply(w, 200, ds)
		case "qr":
			a.mu.Lock()
			c := a.cfg
			a.mu.Unlock()
			svg, e := qr.SVG(c.MoblinURL())
			if e != nil {
				errorReply(w, e)
				return
			}
			w.Header().Set("Content-Type", "image/svg+xml")
			w.Header().Set("Cache-Control", "no-store")
			_, _ = io.WriteString(w, svg)
		case "diagnostics":
			a.diagnostics(w)
		default:
			http.NotFound(w, r)
		}
		return
	}
	if r.Method != "POST" {
		http.Error(w, "Method not allowed", 405)
		return
	}
	switch path {
	case "config":
		var c engine.Config
		if e := readJSON(w, r, &c); e != nil {
			errorReply(w, e)
			return
		}
		if e := c.Validate(); e != nil {
			errorReply(w, e)
			return
		}
		a.mu.Lock()
		if a.working {
			a.mu.Unlock()
			errorReply(w, errors.New("Во время записи настройки заблокированы"))
			return
		}
		if e := engine.AtomicJSON(filepath.Join(a.cache, "settings.json"), c); e != nil {
			a.mu.Unlock()
			errorReply(w, e)
			return
		}
		a.cfg = c
		a.mu.Unlock()
		jsonReply(w, 200, map[string]bool{"ok": true})
	case "start":
		a.mu.Lock()
		c := a.cfg
		a.mu.Unlock()
		if e := c.Validate(); e != nil {
			errorReply(w, e)
			return
		}
		ctx, e := a.jobStart()
		if e != nil {
			errorReply(w, e)
			return
		}
		go func() {
			defer a.jobDone()
			folder, er := a.recorder.Record(ctx, c)
			if folder != "" {
				a.mu.Lock()
				a.lastFolder = folder
				a.mu.Unlock()
				_ = os.WriteFile(filepath.Join(a.cache, "last-take.txt"), []byte(folder), 0600)
			}
			if er != nil {
				a.recorder.Log(er.Error())
				return
			}
			if c.AutoExport && c.Microphone != "" {
				exportCtx, cancel := context.WithCancel(context.Background())
				defer cancel()
				a.mu.Lock()
				a.cancel = cancel
				t := a.tools
				a.mu.Unlock()
				a.recorder.ExportState(true, "Синхронизация и сборка итогового файла")
				files, er := t.Export(exportCtx, folder, c, a.recorder.Log)
				if er != nil {
					a.recorder.Log(er.Error())
					a.recorder.ExportState(false, "Исходники сохранены. Сборка требует внимания: "+er.Error())
				} else {
					a.recorder.ExportState(false, fmt.Sprintf("Готово. Создано файлов: %d", len(files)))
				}
			}
		}()
		jsonReply(w, 202, map[string]bool{"ok": true})
	case "stop":
		a.mu.Lock()
		if a.cancel != nil {
			a.cancel()
		}
		a.mu.Unlock()
		jsonReply(w, 200, map[string]bool{"ok": true})
	case "export":
		var request struct {
			Folder string `json:"folder"`
		}
		if e := readJSON(w, r, &request); e != nil {
			errorReply(w, e)
			return
		}
		a.mu.Lock()
		if request.Folder == "" {
			request.Folder = a.lastFolder
		}
		c := a.cfg
		t := a.tools
		a.mu.Unlock()
		if !filepath.IsAbs(request.Folder) {
			errorReply(w, errors.New("Укажите полный путь к папке дубля"))
			return
		}
		if _, e := engine.ReadManifest(request.Folder); e != nil {
			errorReply(w, e)
			return
		}
		ctx, e := a.jobStart()
		if e != nil {
			errorReply(w, e)
			return
		}
		go func() {
			defer a.jobDone()
			a.recorder.ExportState(true, "Синхронизация и сборка")
			files, er := t.Export(ctx, request.Folder, c, a.recorder.Log)
			if er != nil {
				a.recorder.Log(er.Error())
				a.recorder.ExportState(false, er.Error())
			} else {
				a.recorder.ExportState(false, fmt.Sprintf("Собрано файлов: %d", len(files)))
			}
		}()
		jsonReply(w, 202, map[string]bool{"ok": true})
	case "prepare":
		if runtime.GOOS != "windows" {
			errorReply(w, errors.New("Автоустановка движка доступна в Windows-сборке"))
			return
		}
		ctx, e := a.jobStart()
		if e != nil {
			errorReply(w, e)
			return
		}
		go func() {
			defer a.jobDone()
			a.recorder.ExportState(true, "Загрузка проверенной сборки FFmpeg")
			t, er := engine.InstallTools(ctx, a.cache, a.recorder.Log)
			if er != nil {
				a.recorder.Log(er.Error())
				a.recorder.ExportState(false, er.Error())
				return
			}
			a.mu.Lock()
			a.tools = t
			a.mu.Unlock()
			a.recorder.SetTools(t)
			a.recorder.ExportState(false, "Движок готов. Обновите список микрофонов.")
		}()
		jsonReply(w, 202, map[string]bool{"ok": true})
	case "mic-test":
		a.mu.Lock()
		c := a.cfg
		t := a.tools
		busy := a.working
		a.mu.Unlock()
		if busy {
			errorReply(w, errors.New("Сначала остановите запись"))
			return
		}
		if c.Microphone == "" {
			errorReply(w, errors.New("Сначала выберите микрофон"))
			return
		}
		job, e := a.jobStart()
		if e != nil {
			errorReply(w, e)
			return
		}
		defer a.jobDone()
		ctx, cancel := context.WithTimeout(job, 10*time.Second)
		defer cancel()
		b, e := engine.Run(ctx, t.FFmpeg, []string{"-hide_banner", "-f", "dshow", "-audio_buffer_size", "100", "-i", "audio=" + c.Microphone, "-t", "3", "-af", "volumedetect", "-f", "null", "-"}, "")
		if e != nil {
			errorReply(w, e)
			return
		}
		lines := []string{}
		for _, l := range strings.Split(string(b), "\n") {
			if strings.Contains(l, "mean_volume") || strings.Contains(l, "max_volume") {
				lines = append(lines, l)
			}
		}
		jsonReply(w, 200, map[string]string{"message": "Микрофон работает. " + strings.Join(lines, "\n")})
	case "browse":
		p, e := chooseFolder()
		if e != nil {
			errorReply(w, e)
			return
		}
		jsonReply(w, 200, map[string]string{"path": p})
	case "open-folder":
		var req struct {
			Folder string `json:"folder"`
		}
		if e := readJSON(w, r, &req); e != nil {
			errorReply(w, e)
			return
		}
		a.mu.Lock()
		if req.Folder == "" {
			req.Folder = a.lastFolder
		}
		if req.Folder == "" {
			req.Folder = a.cfg.OutputDir
		}
		a.mu.Unlock()
		if !filepath.IsAbs(req.Folder) {
			errorReply(w, errors.New("Некорректный путь"))
			return
		}
		if e := openFolder(req.Folder); e != nil {
			errorReply(w, e)
			return
		}
		jsonReply(w, 200, map[string]bool{"ok": true})
	case "firewall":
		a.mu.Lock()
		c := a.cfg
		t := a.tools
		busy := a.working
		a.mu.Unlock()
		if busy {
			errorReply(w, errors.New("Настраивайте сеть до начала записи"))
			return
		}
		if e := c.Validate(); e != nil {
			errorReply(w, e)
			return
		}
		if e := firewall(c, t); e != nil {
			errorReply(w, e)
			return
		}
		jsonReply(w, 200, map[string]string{"message": "Правило SRT добавлено только для частной сети и локальной подсети"})
	case "shutdown":
		a.mu.Lock()
		busy := a.working
		a.mu.Unlock()
		if busy {
			errorReply(w, errors.New("Сначала остановите запись или сборку"))
			return
		}
		jsonReply(w, 200, map[string]bool{"ok": true})
		a.quitOnce.Do(func() { close(a.quit) })
	default:
		http.NotFound(w, r)
	}
}
func (a *App) diagnostics(w http.ResponseWriter) {
	a.mu.Lock()
	c := a.cfg
	c.Passphrase = ""
	last := a.lastFolder
	a.mu.Unlock()
	s := a.recorder.Snapshot()
	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition", "attachment; filename=AirTake-diagnostics.zip")
	z := zip.NewWriter(w)
	defer z.Close()
	for name, v := range map[string]any{"settings-redacted.json": c, "status.json": s} {
		f, e := z.Create(name)
		if e == nil {
			_ = json.NewEncoder(f).Encode(v)
		}
	}
	if last != "" {
		if b, e := os.ReadFile(filepath.Join(last, "take.json")); e == nil && len(b) < 1024*1024 {
			f, e := z.Create("take.json")
			if e == nil {
				_, _ = f.Write(b)
			}
		}
	}
}
