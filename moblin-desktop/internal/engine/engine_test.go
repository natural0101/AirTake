package engine

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"math"
	"math/rand"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestConfig(t *testing.T) {
	c := DefaultConfig()
	if e := c.Validate(); e != nil {
		t.Fatal(e)
	}
	c.Port = 0
	if c.Validate() == nil {
		t.Fatal("bad port accepted")
	}
	c = DefaultConfig()
	c.Passphrase = "short"
	if c.Validate() == nil {
		t.Fatal("bad password accepted")
	}
	c = DefaultConfig()
	c.Address = "0.0.0.0"
	if c.Validate() == nil {
		t.Fatal("wildcard binding accepted")
	}
}
func TestSRTUnitsAndBuffer(t *testing.T) {
	c := DefaultConfig()
	u, e := url.Parse(c.ReceiveURL())
	if e != nil {
		t.Fatal(e)
	}
	if u.Query().Get("latency") != "1200000" {
		t.Fatal(u)
	}
	if strings.Contains(c.SendURL(), "latency=") {
		t.Fatal("do not mix ffmpeg microseconds with Moblin ms")
	}
}
func TestMoblinPayload(t *testing.T) {
	c := DefaultConfig()
	s, e := url.QueryUnescape(strings.TrimPrefix(c.MoblinURL(), "moblin://?"))
	if e != nil {
		t.Fatal(e)
	}
	var p map[string]any
	if json.Unmarshal([]byte(s), &p) != nil {
		t.Fatal(s)
	}
	stream := p["streams"].([]any)[0].(map[string]any)
	v := stream["video"].(map[string]any)
	if v["fps"] != float64(120) || v["bitrate"] != float64(120000000) {
		t.Fatal(v)
	}
}
func TestNoInterpolationOrReencode(t *testing.T) {
	c := DefaultConfig()
	for _, port := range []int{0, 12345} {
		a := ReceiveArgs(c, "source.mkv", port)
		s := strings.Join(a, " ")
		for _, bad := range []string{" -r ", " fps=", "libx265", "libx264", "scale="} {
			if strings.Contains(" "+s+" ", bad) {
				t.Fatal(s)
			}
		}
		if !strings.Contains(s, "-c copy") {
			t.Fatal(s)
		}
	}
}
func TestDeviceParsing(t *testing.T) {
	s := "[dshow @ x] \"Webcam\" (video)\n[dshow @ x] Alternative name \"@video\"\n[dshow @ x] \"Микрофон (FIFINE)\" (audio)\n[dshow @ x] Alternative name \"@device_cm_abc\"\n[dshow @ x] \"Other\" (audio)"
	d := ParseDevices(s)
	if len(d) != 2 || d[0].ID != "@device_cm_abc" || d[1].ID != "Other" {
		t.Fatal(d)
	}
}
func TestFormatParsing(t *testing.T) {
	s, w, h, f := ParseVideo("Stream #0:0: Video: hevc (Main), yuv420p(tv), 3840x2160 [SAR 1:1 DAR 16:9], 120 fps, 120 tbr")
	if s == "" || w != 3840 || h != 2160 || f != 120 {
		t.Fatal(s, w, h, f)
	}
}
func TestRedaction(t *testing.T) {
	s := Redact("srt://1:5000?passphrase=supersecret&pbkeylen=16")
	if strings.Contains(s, "supersecret") || !strings.Contains(s, "pbkeylen=16") {
		t.Fatal(s)
	}
}
func TestCorrelation(t *testing.T) {
	r := rand.New(rand.NewSource(42))
	search := make([]float64, 4000)
	for i := range search {
		search[i] = r.Float64()
	}
	ref := append([]float64{}, search[731:2731]...)
	for i := range ref {
		ref[i] = ref[i]*0.4 + 0.8
	}
	m, e := Correlate(ref, search)
	if e != nil || m.Index != 731 || m.Score < .999 {
		t.Fatal(m, e)
	}
}
func TestCorrelationRejectsSilence(t *testing.T) {
	_, e := Correlate(make([]float64, 300), make([]float64, 500))
	if e == nil {
		t.Fatal("silence accepted")
	}
}
func TestCorrelationRejectsAmbiguous(t *testing.T) {
	ref := make([]float64, 300)
	for i := range ref {
		ref[i] = math.Sin(float64(i) * .1)
	}
	search := append(append(append([]float64{}, ref...), make([]float64, 100)...), ref...)
	if _, e := Correlate(ref, search); e == nil {
		t.Fatal("duplicate match accepted")
	}
}
func TestEnvelope(t *testing.T) {
	b := make([]byte, 800*4)
	for i := 0; i < 800; i++ {
		binary.LittleEndian.PutUint32(b[i*4:], math.Float32bits(.5))
	}
	a := Envelope(b)
	if len(a) != 10 || math.Abs(a[0]-math.Log1p(50)) > 1e-6 {
		t.Fatal(a)
	}
}
func TestSafePaths(t *testing.T) {
	for _, n := range []string{"../other", "a/b", "a\\b", "C:secret", "."} {
		if _, e := SafePartPath(t.TempDir(), n); e == nil {
			t.Fatal(n)
		}
	}
}
func TestAtomicJSON(t *testing.T) {
	p := filepath.Join(t.TempDir(), "settings.json")
	for _, v := range []string{"first", "second"} {
		if e := AtomicJSON(p, map[string]string{"v": v}); e != nil {
			t.Fatal(e)
		}
	}
	b, _ := os.ReadFile(p)
	if !strings.Contains(string(b), "second") {
		t.Fatal(string(b))
	}
}
func TestAudioDelaySign(t *testing.T) {
	a := ExportArgs("v", "m", "o", MediaInfo{Duration: 10, Codec: "hevc"}, Alignment{Offset: 2, Tempo: 1}, 500, "mp4")
	s := strings.Join(a, " ")
	if !strings.Contains(s, "-ss 1.500000") || !strings.Contains(s, "-c:v copy") || !strings.Contains(s, "-tag:v hvc1") {
		t.Fatal(s)
	}
}
func TestNegativeAudioOffset(t *testing.T) {
	s := strings.Join(ExportArgs("v", "m", "o", MediaInfo{Duration: 10}, Alignment{Offset: -.3, Tempo: 1}, 200, "mkv"), " ")
	if !strings.Contains(s, "adelay=500:all=1") {
		t.Fatal(s)
	}
}
func TestProbeRealFile(t *testing.T) {
	if os.Getenv("AIRTAKE_MEDIA_TESTS") == "" {
		t.Skip("set AIRTAKE_MEDIA_TESTS=1")
	}
	tools := FindTools(".", ".")
	if tools.FFmpeg == "" {
		t.Skip("ffmpeg missing")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	f := filepath.Join(t.TempDir(), "test.mkv")
	_, e := Run(ctx, tools.FFmpeg, []string{"-v", "error", "-f", "lavfi", "-i", "color=size=128x72:rate=120", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", f}, "")
	if e != nil {
		t.Fatal(e)
	}
	m, e := tools.Probe(ctx, f)
	if e != nil || m.FPS != 120 || m.Width != 128 {
		t.Fatal(m, e)
	}
}
