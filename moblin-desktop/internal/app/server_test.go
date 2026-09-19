package app

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/natural0101/AirTake/internal/engine"
)

func testApp(t *testing.T) *App {
	t.Helper()
	return &App{cfg: engine.DefaultConfig(), recorder: engine.NewRecorder(engine.Tools{}), cache: t.TempDir(), token: strings.Repeat("a", 48), origin: "http://127.0.0.1:32000", quit: make(chan struct{})}
}
func TestAPIAuthentication(t *testing.T) {
	a := testApp(t)
	for _, tc := range []struct {
		name, host, origin, token string
		want                      int
	}{
		{"no token", "127.0.0.1:32000", "", "", 401},
		{"wrong token", "127.0.0.1:32000", "", "bad", 401},
		{"host rebinding", "attacker.test", "", a.token, 403},
		{"cross origin", "127.0.0.1:32000", "https://attacker.test", a.token, 403},
		{"same origin", "127.0.0.1:32000", a.origin, a.token, 200},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := httptest.NewRequest("GET", a.origin+"/api/state", nil)
			r.Host = tc.host
			r.Header.Set("Origin", tc.origin)
			r.Header.Set("X-AirTake-Token", tc.token)
			w := httptest.NewRecorder()
			a.headers(http.HandlerFunc(a.api)).ServeHTTP(w, r)
			if w.Code != tc.want {
				t.Fatalf("got %d want %d", w.Code, tc.want)
			}
		})
	}
}
func TestJobExclusion(t *testing.T) {
	a := testApp(t)
	ctx, e := a.jobStart()
	if e != nil {
		t.Fatal(e)
	}
	if _, e = a.jobStart(); e == nil {
		t.Fatal("two jobs admitted")
	}
	a.cancel()
	if ctx.Err() != context.Canceled {
		t.Fatal("not canceled")
	}
	a.jobDone()
	if _, e = a.jobStart(); e != nil {
		t.Fatal(e)
	}
	a.cancel()
	a.jobDone()
}
func TestQRAndSecurityHeaders(t *testing.T) {
	a := testApp(t)
	r := httptest.NewRequest("GET", a.origin+"/api/qr", nil)
	r.Header.Set("X-AirTake-Token", a.token)
	w := httptest.NewRecorder()
	a.headers(http.HandlerFunc(a.api)).ServeHTTP(w, r)
	if w.Code != 200 || !strings.Contains(w.Body.String(), "<svg") {
		t.Fatal(w.Code)
	}
	if w.Header().Get("X-Frame-Options") != "DENY" || !strings.Contains(w.Header().Get("Content-Security-Policy"), "script-src 'self'") {
		t.Fatal("security headers missing")
	}
}
