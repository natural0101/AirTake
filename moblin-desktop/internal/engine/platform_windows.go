//go:build windows

package engine

import (
	"os/exec"
	"syscall"
	"unsafe"
)

func configureProcess(c *exec.Cmd) { c.SysProcAttr = &syscall.SysProcAttr{HideWindow: true} }
func FreeDisk(path string) (uint64, error) {
	p, e := syscall.UTF16PtrFromString(path)
	if e != nil {
		return 0, e
	}
	var free, total, all uint64
	r, _, er := syscall.NewLazyDLL("kernel32.dll").NewProc("GetDiskFreeSpaceExW").Call(uintptr(unsafe.Pointer(p)), uintptr(unsafe.Pointer(&free)), uintptr(unsafe.Pointer(&total)), uintptr(unsafe.Pointer(&all)))
	if r == 0 {
		return 0, er
	}
	return free, nil
}
