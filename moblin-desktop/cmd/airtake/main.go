package main

import (
	"fmt"
	"github.com/natural0101/AirTake/internal/app"
	"os"
)

func main() {
	if len(os.Args) > 1 && os.Args[1] == "--version" {
		fmt.Println("AirTake " + app.Version)
		return
	}
	if e := app.Run(); e != nil {
		app.ShowError(e)
		os.Exit(1)
	}
}
