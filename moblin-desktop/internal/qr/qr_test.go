package qr

import (
	"crypto/sha256"
	"fmt"
	"strings"
	"testing"
)

func TestGoldenMatrices(t *testing.T) {
	cases := []struct{ s, hash string }{
		{"AirTake", "c3621c1d4f8aa21463401d127f748d240a27441bf0fc3cfdde6e3d600c5ba4c0"},
		{"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "84f14ba5c7d0a590fea3fc4ed2834c4ac37bc2acb879a4a40c2211fdb74c0f1e"},
		{"moblin://?AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-AirTake-profile-", "bf5d1857928b1d76ddae537f0923f7f6bb07050717c7d868e02ad51f59c8aac7"},
	}
	for _, c := range cases {
		m, e := Matrix(c.s)
		if e != nil {
			t.Fatal(e)
		}
		var b strings.Builder
		for _, row := range m {
			for _, v := range row {
				if v {
					b.WriteByte('1')
				} else {
					b.WriteByte('0')
				}
			}
		}
		got := fmt.Sprintf("%x", sha256.Sum256([]byte(b.String())))
		if got != c.hash {
			t.Fatalf("matrix len=%d got %s want %s", len(c.s), got, c.hash)
		}
	}
}
func TestBound(t *testing.T) {
	if _, e := Matrix(strings.Repeat("x", 5000)); e == nil {
		t.Fatal("oversize accepted")
	}
}
