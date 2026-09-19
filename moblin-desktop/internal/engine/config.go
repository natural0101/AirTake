package engine

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

type Config struct {
	Address        string  `json:"address"`
	Port           int     `json:"port"`
	LatencyMS      int     `json:"latencyMs"`
	BitrateMbps    int     `json:"bitrateMbps"`
	FPS            int     `json:"fps"`
	Width          int     `json:"width"`
	Height         int     `json:"height"`
	Passphrase     string  `json:"passphrase"`
	OutputDir      string  `json:"outputDir"`
	Microphone     string  `json:"microphone"`
	MicrophoneName string  `json:"microphoneName"`
	AutoReconnect  bool    `json:"autoReconnect"`
	AutoExport     bool    `json:"autoExport"`
	SyncMode       string  `json:"syncMode"`
	AudioDelayMS   int     `json:"audioDelayMs"`
	Container      string  `json:"container"`
	ReserveGB      float64 `json:"reserveGb"`
	Preview        bool    `json:"preview"`
	MaxMinutes     int     `json:"maxMinutes"`
}

type Address struct {
	IP   string `json:"ip"`
	Name string `json:"name"`
}

func Addresses() []Address {
	out := []Address{}
	ifaces, _ := net.Interfaces()
	for _, n := range ifaces {
		if n.Flags&net.FlagUp == 0 || n.Flags&net.FlagLoopback != 0 {
			continue
		}
		as, _ := n.Addrs()
		for _, a := range as {
			ip, _, e := net.ParseCIDR(a.String())
			if e == nil && ip.To4() != nil && !ip.IsLinkLocalUnicast() {
				out = append(out, Address{ip.String(), n.Name})
			}
		}
	}
	return out
}
func DefaultConfig() Config {
	home, _ := os.UserHomeDir()
	b := make([]byte, 12)
	_, _ = rand.Read(b)
	c := Config{Address: "127.0.0.1", Port: 5000, LatencyMS: 1200, BitrateMbps: 120, FPS: 120, Width: 3840, Height: 2160, Passphrase: hex.EncodeToString(b), OutputDir: filepath.Join(home, "Videos", "AirTake"), AutoReconnect: true, AutoExport: true, SyncMode: "auto", Container: "mkv", ReserveGB: 3}
	if as := Addresses(); len(as) > 0 {
		c.Address = as[0].IP
	}
	return c
}
func (c Config) Validate() error {
	ip := net.ParseIP(c.Address)
	if ip == nil || ip.To4() == nil || ip.IsUnspecified() || ip.IsMulticast() {
		return errors.New("Выберите IPv4-адрес компьютера, не 0.0.0.0")
	}
	if c.Port < 1024 || c.Port > 65535 {
		return errors.New("Порт: 1024–65535")
	}
	if c.LatencyMS < 200 || c.LatencyMS > 5000 {
		return errors.New("Буфер SRT: 200–5000 мс")
	}
	if c.BitrateMbps < 10 || c.BitrateMbps > 200 {
		return errors.New("Битрейт: 10–200 Мбит/с")
	}
	if c.FPS != 30 && c.FPS != 60 && c.FPS != 120 {
		return errors.New("Профиль FPS: 30, 60 или 120")
	}
	if !((c.Width == 3840 && c.Height == 2160) || (c.Width == 1920 && c.Height == 1080)) {
		return errors.New("Разрешение: 3840×2160 или 1920×1080")
	}
	if len(c.Passphrase) > 0 && (len(c.Passphrase) < 10 || len(c.Passphrase) > 79) {
		return errors.New("Пароль SRT: 10–79 ASCII-символов или пустое поле")
	}
	for _, r := range c.Passphrase {
		if r < 33 || r > 126 {
			return errors.New("Пароль SRT: только печатные ASCII-символы без пробелов")
		}
	}
	if strings.TrimSpace(c.OutputDir) == "" || !filepath.IsAbs(c.OutputDir) {
		return errors.New("Укажите полный путь к папке записи")
	}
	if c.ReserveGB < 1 || c.ReserveGB > 1000 {
		return errors.New("Резерв диска: 1–1000 ГБ")
	}
	if c.Container != "mkv" && c.Container != "mp4" {
		return errors.New("Контейнер: MKV или MP4")
	}
	if c.SyncMode != "auto" && c.SyncMode != "manual" {
		return errors.New("Неизвестный режим синхронизации")
	}
	if c.AudioDelayMS < -60000 || c.AudioDelayMS > 60000 {
		return errors.New("Сдвиг звука: от −60000 до +60000 мс")
	}
	if c.MaxMinutes < 0 || c.MaxMinutes > 1440 {
		return errors.New("Лимит записи: 0–1440 минут")
	}
	return nil
}
func (c Config) ReceiveURL() string {
	q := url.Values{"mode": {"listener"}, "transtype": {"live"}, "latency": {strconv.Itoa(c.LatencyMS * 1000)}, "timeout": {"8000000"}, "peeridletimeo": {"5000"}}
	// The SRT receive buffer must cover the configured latency at high bitrates.
	b := int64(c.BitrateMbps) * 1000000 / 8 * int64(c.LatencyMS) / 1000 * 2
	if b < 32*1024*1024 {
		b = 32 * 1024 * 1024
	}
	q.Set("rcvbuf", strconv.FormatInt(b, 10))
	q.Set("ffs", strconv.FormatInt(b*2, 10))
	if c.Passphrase != "" {
		q.Set("passphrase", c.Passphrase)
		q.Set("pbkeylen", "16")
	}
	return fmt.Sprintf("srt://%s:%d?%s", c.Address, c.Port, q.Encode())
}
func (c Config) SendURL() string {
	q := url.Values{"mode": {"caller"}}
	if c.Passphrase != "" {
		q.Set("passphrase", c.Passphrase)
		q.Set("pbkeylen", "16")
	}
	return fmt.Sprintf("srt://%s:%d?%s", c.Address, c.Port, q.Encode())
}
func (c Config) MoblinURL() string {
	// Field names and values follow MoblinSettingsUrl.swift / SettingsStream.swift.
	payload := map[string]any{"streams": []any{map[string]any{"name": "AirTake", "url": c.SendURL(), "selected": true, "video": map[string]any{"resolution": fmt.Sprintf("%dx%d", c.Width, c.Height), "fps": c.FPS, "codec": "H.265/HEVC", "bitrate": c.BitrateMbps * 1000000, "bFrames": false, "maxKeyFrameInterval": 1}, "audio": map[string]any{"bitrate": 128000}, "srt": map[string]any{"latency": c.LatencyMS, "adaptiveBitrateEnabled": false}}}}
	b, _ := json.Marshal(payload)
	return "moblin://?" + strings.ReplaceAll(url.QueryEscape(string(b)), "+", "%20")
}
func AtomicJSON(path string, v any) error {
	b, e := json.MarshalIndent(v, "", "  ")
	if e != nil {
		return e
	}
	if e = os.MkdirAll(filepath.Dir(path), 0700); e != nil {
		return e
	}
	tmp := path + ".tmp"
	f, e := os.OpenFile(tmp, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0600)
	if e != nil {
		return e
	}
	if _, e = f.Write(b); e == nil {
		e = f.Sync()
	}
	ce := f.Close()
	if e != nil {
		return e
	}
	if ce != nil {
		return ce
	}
	return os.Rename(tmp, path)
}
func LoadConfig(path string) Config {
	c := DefaultConfig()
	b, e := os.ReadFile(path)
	if e == nil {
		if json.Unmarshal(b, &c) != nil {
			return DefaultConfig()
		}
	}
	return c
}
