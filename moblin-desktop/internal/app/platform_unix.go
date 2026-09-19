//go:build !windows

package app

import (
	"errors"
	"fmt"
	"github.com/natural0101/AirTake/internal/engine"
	"os/exec"
)

func openBrowser(u, cache string) error { return exec.Command("xdg-open", u).Start() }
func openFolder(p string) error         { return exec.Command("xdg-open", p).Start() }
func chooseFolder() (string, error) {
	return "", errors.New("Введите путь к папке вручную")
}
func firewall(c engine.Config, t engine.Tools) error {
	return errors.New("Настройка брандмауэра доступна в Windows")
}
func ShowError(e error) { fmt.Println(e) }
