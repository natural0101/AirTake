// Package qr implements a bounded QR Code byte-mode encoder at error correction L.
// It has no network access or third-party runtime dependency. Mask 0 is deliberate:
// every valid QR mask is decodable; golden tests compare every module to python-qrcode.
package qr

import (
	"errors"
	"fmt"
	"strings"
)

type bits []bool

func (b *bits) put(v, n int) {
	for i := n - 1; i >= 0; i-- {
		*b = append(*b, (v>>i)&1 != 0)
	}
}
func multiply(a, b byte) byte {
	var r byte
	for b != 0 {
		if b&1 != 0 {
			r ^= a
		}
		high := a & 128
		a <<= 1
		if high != 0 {
			a ^= 0x1d
		}
		b >>= 1
	}
	return r
}
func ecc(data []byte, count int) []byte {
	generator := []byte{1}
	root := byte(1)
	for i := 0; i < count; i++ {
		next := make([]byte, len(generator)+1)
		for j, c := range generator {
			next[j] ^= c
			next[j+1] ^= multiply(c, root)
		}
		generator = next
		root = multiply(root, 2)
	}
	out := make([]byte, count)
	for _, d := range data {
		factor := d ^ out[0]
		copy(out, out[1:])
		out[count-1] = 0
		for j := 0; j < count; j++ {
			out[j] ^= multiply(generator[j+1], factor)
		}
	}
	return out
}
func bch(v, poly int) int {
	for degree(v) >= degree(poly) {
		v ^= poly << (degree(v) - degree(poly))
	}
	return v
}
func degree(v int) int {
	d := -1
	for v != 0 {
		d++
		v >>= 1
	}
	return d
}
func abs(i int) int {
	if i < 0 {
		return -i
	}
	return i
}
func Matrix(text string) ([][]bool, error) {
	data := []byte(text)
	version := 0
	capacity := 0
	for i, spec := range blocks {
		cap := 0
		for j := 0; j < len(spec); j += 3 {
			cap += spec[j] * spec[j+2]
		}
		lengthBits := 8
		if i+1 >= 10 {
			lengthBits = 16
		}
		if 4+lengthBits+len(data)*8 <= cap*8 {
			version = i + 1
			capacity = cap
			break
		}
	}
	if version == 0 {
		return nil, errors.New("Профиль слишком большой для QR-кода")
	}
	var b bits
	b.put(4, 4)
	countBits := 8
	if version >= 10 {
		countBits = 16
	}
	b.put(len(data), countBits)
	for _, d := range data {
		b.put(int(d), 8)
	}
	terminator := capacity*8 - len(b)
	if terminator > 4 {
		terminator = 4
	}
	b.put(0, terminator)
	for len(b)%8 != 0 {
		b.put(0, 1)
	}
	raw := make([]byte, len(b)/8)
	for i, v := range b {
		if v {
			raw[i/8] |= 1 << uint(7-i%8)
		}
	}
	for len(raw) < capacity {
		raw = append(raw, 0xec)
		if len(raw) < capacity {
			raw = append(raw, 0x11)
		}
	}
	var chunks, checks [][]byte
	offset := 0
	maxData, maxECC := 0, 0
	spec := blocks[version-1]
	for j := 0; j < len(spec); j += 3 {
		for k := 0; k < spec[j]; k++ {
			n := spec[j+2]
			e := spec[j+1] - n
			chunk := raw[offset : offset+n]
			offset += n
			chunks = append(chunks, chunk)
			checks = append(checks, ecc(chunk, e))
			if n > maxData {
				maxData = n
			}
			if e > maxECC {
				maxECC = e
			}
		}
	}
	stream := []byte{}
	for i := 0; i < maxData; i++ {
		for _, c := range chunks {
			if i < len(c) {
				stream = append(stream, c[i])
			}
		}
	}
	for i := 0; i < maxECC; i++ {
		for _, c := range checks {
			if i < len(c) {
				stream = append(stream, c[i])
			}
		}
	}
	n := version*4 + 17
	m := make([][]int, n)
	for y := range m {
		m[y] = make([]int, n)
		for x := range m[y] {
			m[y][x] = -1
		}
	}
	finder := func(y, x int) {
		for dy := -1; dy <= 7; dy++ {
			for dx := -1; dx <= 7; dx++ {
				r, c := y+dy, x+dx
				if r < 0 || r >= n || c < 0 || c >= n {
					continue
				}
				black := (dy >= 0 && dy <= 6 && dx >= 0 && dx <= 6 && (dy == 0 || dy == 6 || dx == 0 || dx == 6)) || (dy >= 2 && dy <= 4 && dx >= 2 && dx <= 4)
				m[r][c] = 0
				if black {
					m[r][c] = 1
				}
			}
		}
	}
	finder(0, 0)
	finder(n-7, 0)
	finder(0, n-7)
	for _, y := range alignment[version-1] {
		for _, x := range alignment[version-1] {
			if m[y][x] != -1 {
				continue
			}
			for dy := -2; dy <= 2; dy++ {
				for dx := -2; dx <= 2; dx++ {
					m[y+dy][x+dx] = 0
					if abs(dx) == 2 || abs(dy) == 2 || (dy == 0 && dx == 0) {
						m[y+dy][x+dx] = 1
					}
				}
			}
		}
	}
	for i := 8; i < n-8; i++ {
		if m[i][6] == -1 {
			m[i][6] = 1 - i%2
		}
		if m[6][i] == -1 {
			m[6][i] = 1 - i%2
		}
	}
	// Format information: L (01), mask 0; BCH(15,5), XOR format mask.
	f := ((8 << 10) | bch(8<<10, 0x537)) ^ 0x5412
	for i := 0; i < 15; i++ {
		v := (f >> i) & 1
		y := i
		if i >= 6 && i < 8 {
			y = i + 1
		} else if i >= 8 {
			y = n - 15 + i
		}
		m[y][8] = v
		x := n - i - 1
		if i >= 8 && i < 9 {
			x = 15 - i
		} else if i >= 9 {
			x = 14 - i
		}
		m[8][x] = v
	}
	m[n-8][8] = 1
	if version >= 7 {
		v := (version << 12) | bch(version<<12, 0x1f25)
		for i := 0; i < 18; i++ {
			m[i/3][i%3+n-11] = (v >> i) & 1
			m[i%3+n-11][i/3] = (v >> i) & 1
		}
	}
	row, direction, index := n-1, -1, 0
	for col := n - 1; col > 0; col -= 2 {
		if col == 6 {
			col--
		}
		for {
			for j := 0; j < 2; j++ {
				x := col - j
				if m[row][x] != -1 {
					continue
				}
				value := false
				if index < len(stream)*8 {
					value = (stream[index/8]>>uint(7-index%8))&1 != 0
				}
				index++
				if (row+x)%2 == 0 {
					value = !value
				}
				m[row][x] = 0
				if value {
					m[row][x] = 1
				}
			}
			row += direction
			if row < 0 || row >= n {
				row -= direction
				direction = -direction
				break
			}
		}
	}
	out := make([][]bool, n)
	for y := range out {
		out[y] = make([]bool, n)
		for x := range out[y] {
			out[y][x] = m[y][x] == 1
		}
	}
	return out, nil
}
func SVG(text string) (string, error) {
	m, e := Matrix(text)
	if e != nil {
		return "", e
	}
	size := len(m) + 8
	var b strings.Builder
	fmt.Fprintf(&b, `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" shape-rendering="crispEdges"><rect width="100%%" height="100%%" fill="white"/><path fill="black" d="`, size, size)
	for y, row := range m {
		for x, v := range row {
			if v {
				fmt.Fprintf(&b, "M%d %dh1v1h-1z", x+4, y+4)
			}
		}
	}
	b.WriteString(`"/></svg>`)
	return b.String(), nil
}
