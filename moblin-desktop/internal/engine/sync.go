package engine

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

type MediaInfo struct {
	Width    int     `json:"width"`
	Height   int     `json:"height"`
	FPS      float64 `json:"fps"`
	Codec    string  `json:"codec"`
	Duration float64 `json:"duration"`
	Audio    bool    `json:"audio"`
}

func rate(s string) float64 {
	a, b, ok := strings.Cut(s, "/")
	if !ok {
		v, _ := strconv.ParseFloat(s, 64)
		return v
	}
	x, _ := strconv.ParseFloat(a, 64)
	y, _ := strconv.ParseFloat(b, 64)
	if y == 0 {
		return 0
	}
	return x / y
}
func (t Tools) Probe(ctx context.Context, path string) (MediaInfo, error) {
	var m MediaInfo
	b, e := Run(ctx, t.FFprobe, []string{"-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate,r_frame_rate", "-of", "json", path}, "")
	if e != nil {
		return m, e
	}
	var p struct {
		Format struct {
			Duration string `json:"duration"`
		} `json:"format"`
		Streams []struct {
			Type   string `json:"codec_type"`
			Codec  string `json:"codec_name"`
			Width  int    `json:"width"`
			Height int    `json:"height"`
			Avg    string `json:"avg_frame_rate"`
			Rate   string `json:"r_frame_rate"`
		} `json:"streams"`
	}
	if e = json.Unmarshal(b, &p); e != nil {
		return m, e
	}
	m.Duration, _ = strconv.ParseFloat(p.Format.Duration, 64)
	for _, s := range p.Streams {
		if s.Type == "video" && m.Width == 0 {
			m.Width = s.Width
			m.Height = s.Height
			m.Codec = s.Codec
			m.FPS = rate(s.Avg)
			if m.FPS == 0 {
				m.FPS = rate(s.Rate)
			}
		}
		if s.Type == "audio" {
			m.Audio = true
		}
	}
	if m.Width == 0 {
		return m, errors.New("В файле нет видеопотока")
	}
	return m, nil
}
func Envelope(pcm []byte) []float64 {
	const stride = 80 // 10 milliseconds at 8 kHz.
	samples := len(pcm) / 4
	out := make([]float64, samples/stride)
	for i := range out {
		sum := 0.0
		for j := 0; j < stride; j++ {
			x := float64(math.Float32frombits(binary.LittleEndian.Uint32(pcm[(i*stride+j)*4:])))
			if !math.IsNaN(x) && !math.IsInf(x, 0) {
				sum += x * x
			}
		}
		out[i] = math.Log1p(100 * math.Sqrt(sum/stride))
	}
	return out
}

type Match struct {
	Index      int
	Score      float64
	Separation float64
}

func Correlate(reference, search []float64) (Match, error) {
	n := len(reference)
	if n < 100 || len(search) < n {
		return Match{}, errors.New("Недостаточно звука для синхронизации")
	}
	var rs, rss float64
	for _, v := range reference {
		rs += v
		rss += v * v
	}
	rv := rss - rs*rs/float64(n)
	if rv/float64(n) < 1e-5 {
		return Match{}, errors.New("В опорном звуке тишина или постоянный сигнал")
	}
	sums := make([]float64, len(search)+1)
	sqs := make([]float64, len(search)+1)
	for i, v := range search {
		sums[i+1] = sums[i] + v
		sqs[i+1] = sqs[i] + v*v
	}
	scores := make([]float64, len(search)-n+1)
	best := Match{Score: -2}
	for k := range scores {
		ss := sums[k+n] - sums[k]
		variance := sqs[k+n] - sqs[k] - ss*ss/float64(n)
		v := -1.0
		if variance > 1e-9 {
			dot := 0.0
			for j, x := range reference {
				dot += x * search[k+j]
			}
			v = (dot - rs*ss/float64(n)) / math.Sqrt(rv*variance)
		}
		scores[k] = v
		if v > best.Score {
			best = Match{Index: k, Score: v}
		}
	}
	second := -1.0
	for i, v := range scores {
		if absInt(i-best.Index) > 20 && v > second {
			second = v
		}
	}
	best.Separation = best.Score - second
	if best.Score < 0.62 || best.Separation < 0.035 {
		return best, fmt.Errorf("Неоднозначная синхронизация (корреляция %.2f, отличие %.2f)", best.Score, best.Separation)
	}
	return best, nil
}
func absInt(i int) int {
	if i < 0 {
		return -i
	}
	return i
}
func (t Tools) envelope(ctx context.Context, file string, start, duration float64) ([]float64, error) {
	args := []string{"-hide_banner", "-v", "error", "-ss", fmt.Sprintf("%.6f", math.Max(0, start)), "-i", file, "-t", fmt.Sprintf("%.6f", duration), "-map", "0:a:0", "-vn", "-af", "highpass=f=120,lowpass=f=3300", "-ac", "1", "-ar", "8000", "-f", "f32le", "pipe:1"}
	b, e := BinaryOutput(ctx, t.FFmpeg, args)
	if e != nil {
		return nil, e
	}
	return Envelope(b), nil
}

type Alignment struct {
	Offset float64
	Tempo  float64
	Score  float64
	Method string
}

func (t Tools) matchAt(ctx context.Context, video, mic string, at, length, approx, radius float64) (float64, float64, error) {
	ref, e := t.envelope(ctx, video, at, length)
	if e != nil {
		return 0, 0, e
	}
	start := math.Max(0, approx+at-radius)
	search, e := t.envelope(ctx, mic, start, length+radius*2)
	if e != nil {
		return 0, 0, e
	}
	m, e := Correlate(ref, search)
	if e != nil {
		return 0, m.Score, e
	}
	return start + float64(m.Index)*0.01 - at, m.Score, nil
}
func (t Tools) Align(ctx context.Context, video, mic string, info MediaInfo, approx float64, log func(string)) (Alignment, error) {
	if !info.Audio {
		return Alignment{}, errors.New("В Moblin выключен опорный звук. Автосинхронизация невозможна; выберите ручной режим")
	}
	if info.Duration < 4 {
		return Alignment{}, errors.New("Для автосинхронизации нужен дубль от 4 секунд с разборчивым звуком")
	}
	length := math.Min(20, info.Duration-2)
	at := 1.0
	offset, score, e := t.matchAt(ctx, video, mic, at, length, approx, 20)
	if e != nil {
		return Alignment{}, e
	}
	a := Alignment{Offset: offset, Tempo: 1, Score: score, Method: "audio-correlation"}
	if info.Duration > 65 {
		end := info.Duration - length - 1
		endOffset, endScore, er := t.matchAt(ctx, video, mic, end, length, offset, 3)
		if er == nil {
			tempo := 1 + (endOffset-offset)/(end-at)
			if tempo < 0.995 || tempo > 1.005 {
				return Alignment{}, errors.New("Слишком большое расхождение часов. Проверьте опорный звук и используйте ручную синхронизацию")
			}
			a.Tempo = tempo
			a.Offset = offset - (tempo-1)*at
			a.Score = math.Min(score, endScore)
			a.Method = "audio-correlation+clock-drift"
		} else {
			log("Дрейф часов в конце дубля не подтверждён: " + er.Error())
		}
	}
	return a, nil
}
func SafePartPath(folder, name string) (string, error) {
	if name == "" || name == "." || filepath.Base(name) != name || strings.ContainsAny(name, "/\\:\x00") {
		return "", errors.New("Недопустимое имя файла в take.json")
	}
	return filepath.Join(folder, name), nil
}
func ReadManifest(folder string) (*Manifest, error) {
	b, e := os.ReadFile(filepath.Join(folder, "take.json"))
	if e != nil {
		return nil, e
	}
	var m Manifest
	if e = json.Unmarshal(b, &m); e != nil {
		return nil, e
	}
	if m.Version != 1 {
		return nil, errors.New("Неподдерживаемая версия take.json")
	}
	m.Folder = folder
	return &m, nil
}
func ExportArgs(video, mic, out string, info MediaInfo, a Alignment, delayMS int, container string) []string {
	// Positive delay moves the microphone later. Video packets are never re-encoded.
	offset := a.Offset - float64(delayMS)/1000*a.Tempo
	args := []string{"-hide_banner", "-y", "-v", "warning", "-i", video}
	if offset > 0 {
		args = append(args, "-ss", fmt.Sprintf("%.6f", offset))
	}
	args = append(args, "-i", mic, "-map", "0:v:0", "-map", "1:a:0", "-map_metadata", "-1", "-c:v", "copy")
	filter := fmt.Sprintf("asetpts=PTS-STARTPTS,atempo=%.9f", a.Tempo)
	if offset < 0 {
		filter += fmt.Sprintf(",adelay=%d:all=1", int(math.Round(-offset/a.Tempo*1000)))
	}
	filter += ",apad"
	args = append(args, "-af", filter, "-t", fmt.Sprintf("%.6f", info.Duration), "-avoid_negative_ts", "make_zero")
	if container == "mp4" {
		args = append(args, "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart")
		if info.Codec == "hevc" {
			args = append(args, "-tag:v", "hvc1")
		}
		args = append(args, "-f", "mp4")
	} else {
		args = append(args, "-c:a", "flac", "-f", "matroska")
	}
	return append(args, out)
}
func (t Tools) Export(ctx context.Context, folder string, c Config, log func(string)) ([]Exported, error) {
	m, e := ReadManifest(folder)
	if e != nil {
		return nil, e
	}
	if m.MicrophoneFile == "" {
		return nil, errors.New("Отдельный микрофон не записывался; видео уже находится в source-*.mkv")
	}
	mic, e := SafePartPath(folder, m.MicrophoneFile)
	if e != nil {
		return nil, e
	}
	if !regular(mic) {
		return nil, errors.New("Файл микрофона не найден")
	}
	result := []Exported{}
	var problems []string
	for i, p := range m.Parts {
		if ctx.Err() != nil {
			return result, ctx.Err()
		}
		video, er := SafePartPath(folder, p.File)
		if er != nil {
			return result, er
		}
		if !regular(video) {
			continue
		}
		pctx, pcancel := context.WithTimeout(ctx, 30*time.Second)
		info, er := t.Probe(pctx, video)
		pcancel()
		if er != nil {
			if p.Frames > 0 {
				problems = append(problems, p.File+": "+er.Error())
			}
			continue
		}
		log(fmt.Sprintf("Сборка %s: %d×%d / %.3f FPS", p.File, info.Width, info.Height, info.FPS))
		a := Alignment{Offset: p.ApproxMicOffset, Tempo: 1, Method: "manual-receiver-clock-estimate"}
		if c.SyncMode == "auto" {
			actx, acancel := context.WithTimeout(ctx, 3*time.Minute)
			a, er = t.Align(actx, video, mic, info, p.ApproxMicOffset, log)
			acancel()
			if er != nil {
				msg := p.File + ": " + er.Error() + ". Исходники сохранены. Переключите синхронизацию в ручной режим и повторите сборку."
				log(msg)
				problems = append(problems, msg)
				continue
			}
		} else if !p.ClockEstimated {
			problems = append(problems, p.File+": нет оценки начала записи для ручной синхронизации")
			continue
		}
		name := fmt.Sprintf("take-%04d_%s.%s", i+1, time.Now().Format("150405.000"), c.Container)
		out := filepath.Join(folder, name)
		tmp := filepath.Join(folder, "building-"+name)
		_, er = Run(ctx, t.FFmpeg, ExportArgs(video, mic, tmp, info, a, c.AudioDelayMS, c.Container), folder)
		if er != nil {
			problems = append(problems, er.Error())
			continue
		}
		verify, er := t.Probe(ctx, tmp)
		if er != nil || verify.Width != info.Width || verify.Height != info.Height || !verify.Audio {
			problems = append(problems, "Проверка готового файла не пройдена: "+tmp)
			continue
		}
		if er = os.Rename(tmp, out); er != nil {
			problems = append(problems, er.Error())
			continue
		}
		item := Exported{Source: p.File, File: name, Sync: a.Method, OffsetSeconds: a.Offset, Tempo: a.Tempo, Confidence: a.Score}
		result = append(result, item)
		m.Exports = append(m.Exports, item)
		if er = AtomicJSON(filepath.Join(folder, "take.json"), m); er != nil {
			return result, er
		}
		log("Готово: " + name + ". В итоговом файле только выбранный микрофон.")
	}
	if len(problems) > 0 {
		return result, errors.New(strings.Join(problems, "\n"))
	}
	if len(result) == 0 {
		return result, errors.New("Нет пригодных видеочастей для сборки")
	}
	return result, nil
}
