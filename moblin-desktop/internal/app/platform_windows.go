//go:build windows

package app

import (
	"encoding/base64"
	"errors"
	"fmt"
	"github.com/natural0101/AirTake/internal/engine"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"unicode/utf16"
	"unsafe"
)

func openBrowser(u, cache string) error {
	for _, root := range []string{os.Getenv("ProgramFiles(x86)"), os.Getenv("ProgramFiles")} {
		p := filepath.Join(root, "Microsoft", "Edge", "Application", "msedge.exe")
		if _, e := os.Stat(p); e == nil {
			return exec.Command(p, "--app="+u, "--no-first-run", "--no-default-browser-check", "--user-data-dir="+filepath.Join(cache, "browser")).Start()
		}
	}
	return exec.Command("rundll32.exe", "url.dll,FileProtocolHandler", u).Start()
}
func openFolder(p string) error {
	if _, e := os.Stat(p); e != nil {
		return e
	}
	return exec.Command("explorer.exe", p).Start()
}
func encodedPS(s string) string {
	u := utf16.Encode([]rune(s))
	b := make([]byte, len(u)*2)
	for i, v := range u {
		b[i*2] = byte(v)
		b[i*2+1] = byte(v >> 8)
	}
	return base64.StdEncoding.EncodeToString(b)
}
func powershell(script string) (string, error) {
	c := exec.Command("powershell.exe", "-NoProfile", "-STA", "-EncodedCommand", encodedPS(script))
	c.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	b, e := c.CombinedOutput()
	if e != nil {
		return "", fmt.Errorf("Windows: %w: %s", e, string(b))
	}
	return strings.TrimSpace(string(b)), nil
}
func chooseFolder() (string, error) {
	return powershell("[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); Add-Type -AssemblyName System.Windows.Forms; $d=[System.Windows.Forms.FolderBrowserDialog]::new(); $d.Description='AirTake'; if($d.ShowDialog() -eq 'OK'){[Console]::Write($d.SelectedPath)}")
}
func firewall(c engine.Config, t engine.Tools) error {
	if t.FFmpeg == "" {
		return errors.New("Сначала установите движок FFmpeg")
	}
	quote := func(s string) string { return "'" + strings.ReplaceAll(s, "'", "''") + "'" }
	name := fmt.Sprintf("AirTake-SRT-%d", c.Port)
	inner := fmt.Sprintf("$ErrorActionPreference='Stop'; Get-NetFirewallRule -Name '%s' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; New-NetFirewallRule -Name '%s' -DisplayName 'AirTake SRT %d' -Group 'AirTake' -Direction Inbound -Action Allow -Protocol UDP -LocalPort %d -LocalAddress %s -Program %s -Profile Private -RemoteAddress LocalSubnet | Out-Null", name, name, c.Port, c.Port, quote(c.Address), quote(t.FFmpeg))
	script := "$ErrorActionPreference='Stop'; $p=Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile','-EncodedCommand'," + quote(encodedPS(inner)) + "; if($p.ExitCode -ne 0){throw 'Firewall configuration failed or was cancelled'}"
	_, e := powershell(script)
	return e
}
func ShowError(e error) {
	text, _ := syscall.UTF16PtrFromString(e.Error())
	title, _ := syscall.UTF16PtrFromString("AirTake")
	syscall.NewLazyDLL("user32.dll").NewProc("MessageBoxW").Call(0, uintptr(unsafe.Pointer(text)), uintptr(unsafe.Pointer(title)), 0x10)
}
