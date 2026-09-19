package engine

import (
	"archive/zip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// The vendor archive is pinned, not a mutable 'latest' download.
const FFmpegURL = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.1-essentials_build.zip"
const FFmpegSHA256 = "fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9"

func InstallTools(ctx context.Context, cache string, log func(string)) (Tools, error) {
	if e := os.MkdirAll(cache, 0700); e != nil {
		return Tools{}, e
	}
	archive, e := os.CreateTemp(cache, "ffmpeg-*.zip")
	if e != nil {
		return Tools{}, e
	}
	defer os.Remove(archive.Name())
	defer archive.Close()
	req, e := http.NewRequestWithContext(ctx, http.MethodGet, FFmpegURL, nil)
	if e != nil {
		return Tools{}, e
	}
	client := &http.Client{Timeout: 10 * time.Minute, CheckRedirect: func(req *http.Request, via []*http.Request) error {
		if len(via) > 5 {
			return errors.New("Слишком много перенаправлений")
		}
		if req.URL.Scheme != "https" {
			return errors.New("Разрешён только HTTPS")
		}
		return nil
	}}
	res, e := client.Do(req)
	if e != nil {
		return Tools{}, e
	}
	defer res.Body.Close()
	if res.StatusCode != 200 {
		return Tools{}, fmt.Errorf("Загрузка FFmpeg: HTTP %d", res.StatusCode)
	}
	hash := sha256.New()
	writer := io.MultiWriter(archive, hash)
	buffer := make([]byte, 256*1024)
	var total, last int64
	for {
		n, er := res.Body.Read(buffer)
		if n > 0 {
			total += int64(n)
			if total > 400*1024*1024 {
				return Tools{}, errors.New("Архив превысил безопасный предел 400 МБ")
			}
			if _, e = writer.Write(buffer[:n]); e != nil {
				return Tools{}, e
			}
			if total-last > 4*1024*1024 {
				log(fmt.Sprintf("Загружено %.0f МБ", float64(total)/1e6))
				last = total
			}
		}
		if er == io.EOF {
			break
		}
		if er != nil {
			return Tools{}, er
		}
	}
	got := hex.EncodeToString(hash.Sum(nil))
	if got != FFmpegSHA256 {
		return Tools{}, fmt.Errorf("SHA-256 архива не совпал. Файл не будет запущен: %s", got)
	}
	if e = archive.Close(); e != nil {
		return Tools{}, e
	}
	z, e := zip.OpenReader(archive.Name())
	if e != nil {
		return Tools{}, e
	}
	defer z.Close()
	temp, e := os.MkdirTemp(cache, "tools-stage-")
	if e != nil {
		return Tools{}, e
	}
	defer os.RemoveAll(temp)
	copied := map[string]bool{}
	for _, f := range z.File {
		name := filepath.Base(strings.ReplaceAll(f.Name, "\\", "/"))
		lower := strings.ToLower(name)
		wanted := lower == "ffmpeg.exe" || lower == "ffprobe.exe" || lower == "ffplay.exe" || strings.HasPrefix(lower, "license") || strings.HasPrefix(lower, "readme")
		if !wanted || f.FileInfo().IsDir() {
			continue
		}
		if f.UncompressedSize64 > 250*1024*1024 {
			return Tools{}, errors.New("Слишком большой файл в архиве")
		}
		src, er := f.Open()
		if er != nil {
			return Tools{}, er
		}
		dst, er := os.OpenFile(filepath.Join(temp, name), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0700)
		if er != nil {
			src.Close()
			return Tools{}, er
		}
		_, er = io.Copy(dst, io.LimitReader(src, 250*1024*1024+1))
		_ = src.Close()
		ce := dst.Close()
		if er != nil {
			return Tools{}, er
		}
		if ce != nil {
			return Tools{}, ce
		}
		copied[lower] = true
	}
	if !copied["ffmpeg.exe"] || !copied["ffprobe.exe"] {
		return Tools{}, errors.New("В архиве отсутствуют обязательные программы")
	}
	notice := "FFmpeg is a separate executable distributed under its own license.\nArchive: " + FFmpegURL + "\nSHA-256: " + got + "\nVendor and build/source information: https://www.gyan.dev/ffmpeg/builds/\nFFmpeg source: https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz\n"
	if e = os.WriteFile(filepath.Join(temp, "AIRTAKE-THIRD-PARTY.txt"), []byte(notice), 0600); e != nil {
		return Tools{}, e
	}
	target := filepath.Join(cache, "tools")
	backup := target + ".previous"
	_ = os.RemoveAll(backup)
	if _, e = os.Stat(target); e == nil {
		if e = os.Rename(target, backup); e != nil {
			return Tools{}, e
		}
	}
	if e = os.Rename(temp, target); e != nil {
		_ = os.Rename(backup, target)
		return Tools{}, e
	}
	_ = os.RemoveAll(backup)
	t := FindTools(cache, cache)
	checkCtx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	if e = t.Check(checkCtx); e != nil {
		return Tools{}, e
	}
	log("FFmpeg установлен; SHA-256 и поддержка SRT проверены")
	return t, nil
}
func AllowedInstanceURL(s string) bool {
	u, e := url.Parse(s)
	return e == nil && u.Scheme == "http" && u.Hostname() == "127.0.0.1" && u.Port() != "" && u.Path == "/"
}
