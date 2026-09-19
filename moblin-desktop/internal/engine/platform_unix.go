//go:build !windows

package engine

import (
	"os/exec"
	"syscall"
)

func configureProcess(c *exec.Cmd) {}
func FreeDisk(path string) (uint64, error) {
	var s syscall.Statfs_t
	e := syscall.Statfs(path, &s)
	return s.Bavail * uint64(s.Bsize), e
}
