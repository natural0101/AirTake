package engine

import (
	"context"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestSRT4K120Passthrough(t *testing.T) {
	if os.Getenv("AIRTAKE_SRT_TESTS") == "" {
		t.Skip("set AIRTAKE_SRT_TESTS=1")
	}
	tools := FindTools(".", ".")
	if tools.FFmpeg == "" {
		t.Fatal("ffmpeg missing")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()
	dir := t.TempDir()
	fixture := filepath.Join(dir, "fixture.ts")
	_, e := Run(ctx, tools.FFmpeg, []string{"-v", "error", "-f", "lavfi", "-i", "color=c=gray:size=3840x2160:rate=120", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000", "-t", "3", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "pools=2:frame-threads=2:log-level=error:keyint=120:bframes=0", "-c:a", "aac", "-f", "mpegts", fixture}, "")
	if e != nil {
		t.Fatal(e)
	}
	conn, e := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 0})
	if e != nil {
		t.Fatal(e)
	}
	port := conn.LocalAddr().(*net.UDPAddr).Port
	conn.Close()
	c := DefaultConfig()
	c.Address = "127.0.0.1"
	c.Port = port
	c.OutputDir = dir
	c.AutoReconnect = false
	c.AutoExport = false
	c.ReserveGB = 1
	c.LatencyMS = 400
	rec := NewRecorder(tools)
	done := make(chan error, 1)
	var folder string
	go func() { var err error; folder, err = rec.Record(ctx, c); done <- err }()
	deadline := time.Now().Add(10 * time.Second)
	for rec.Snapshot().Stage != "listening" && time.Now().Before(deadline) {
		time.Sleep(30 * time.Millisecond)
	}
	time.Sleep(300 * time.Millisecond)
	_, e = Run(ctx, tools.FFmpeg, []string{"-v", "error", "-re", "-i", fixture, "-map", "0", "-c", "copy", "-f", "mpegts", c.SendURL() + "&linger=3"}, "")
	if e != nil {
		cancel()
		<-done
		t.Fatal(e)
	}
	select {
	case e = <-done:
		if e != nil {
			t.Fatal(e)
		}
	case <-time.After(20 * time.Second):
		cancel()
		<-done
		t.Fatal("receiver did not finish after sender disconnect")
	}
	m, e := ReadManifest(folder)
	if e != nil {
		t.Fatal(e)
	}
	if len(m.Parts) == 0 {
		t.Fatal("no recorded parts")
	}
	out := filepath.Join(folder, m.Parts[0].File)
	info, e := tools.Probe(ctx, out)
	if e != nil {
		t.Fatal(e)
	}
	if info.Width != 3840 || info.Height != 2160 || info.FPS != 120 || info.Codec != "hevc" {
		t.Fatal(info)
	}
	count := func(file string) int {
		b, e := Run(ctx, tools.FFprobe, []string{"-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", file}, "")
		if e != nil {
			t.Fatal(e)
		}
		fields := strings.Fields(string(b))
		if len(fields) == 0 {
			t.Fatal("no packet count")
		}
		v, _ := strconv.Atoi(strings.Trim(fields[0], ","))
		return v
	}
	before, after := count(fixture), count(out)
	if before != 360 || after != before {
		t.Fatalf("video packet count: input=%d output=%d\n%+v", before, after, rec.Snapshot())
	}
	t.Logf("PASS: %dx%d HEVC %.0f FPS, %d/%d packets, encrypted SRT -> MKV", info.Width, info.Height, info.FPS, after, before)
}
func TestAudioSyncAndExport(t *testing.T) {
	if os.Getenv("AIRTAKE_MEDIA_TESTS") == "" {
		t.Skip("set AIRTAKE_MEDIA_TESTS=1")
	}
	tools := FindTools(".", ".")
	if tools.FFmpeg == "" {
		t.Fatal("ffmpeg missing")
	}
	ctx, c := context.WithTimeout(context.Background(), 60*time.Second)
	defer c()
	dir := t.TempDir()
	video := filepath.Join(dir, "source.mkv")
	mic := filepath.Join(dir, "microphone.wav")
	// A deterministic amplitude-modulated signal gives non-periodic envelope landmarks.
	expr := "aevalsrc=0.3*sin(2*PI*500*t)*(0.2+0.8*abs(sin(1.73*t)*sin(3.19*t)*sin(0.713*t))):s=48000:d=10"
	_, e := Run(ctx, tools.FFmpeg, []string{"-v", "error", "-f", "lavfi", "-i", "color=size=128x72:rate=120:duration=10", "-f", "lavfi", "-i", expr, "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_s16le", video}, "")
	if e != nil {
		t.Fatal(e)
	}
	_, e = Run(ctx, tools.FFmpeg, []string{"-v", "error", "-i", video, "-vn", "-af", "adelay=2300:all=1,volume=0.6", "-c:a", "pcm_s24le", mic}, "")
	if e != nil {
		t.Fatal(e)
	}
	info, e := tools.Probe(ctx, video)
	if e != nil {
		t.Fatal(e)
	}
	a, e := tools.Align(ctx, video, mic, info, 1.8, func(s string) { t.Log(s) })
	if e != nil {
		t.Fatal(e)
	}
	if a.Offset < 2.27 || a.Offset > 2.33 {
		t.Fatal(a)
	}
	for _, format := range []string{"mkv", "mp4"} {
		out := filepath.Join(dir, "final."+format)
		_, e = Run(ctx, tools.FFmpeg, ExportArgs(video, mic, out, info, a, 0, format), "")
		if e != nil {
			t.Fatal(e)
		}
		v, e := tools.Probe(ctx, out)
		if e != nil || v.FPS != 120 || !v.Audio {
			t.Fatal(v, e)
		}
	}
	t.Log(fmt.Sprintf("PASS: audio sync %.3f s, correlation %.3f; MKV + MP4 export preserves 120 FPS", a.Offset, a.Score))
}
