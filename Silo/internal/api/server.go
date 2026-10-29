// Package api serves Moonbase's /Moonfin routes through Silo's http_routes.v1
// capability. Silo calls Handle over gRPC, and the request is replayed through
// a standard ServeMux so handlers are plain net/http.
package api

import (
	"bytes"
	"context"
	"encoding/json"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strconv"
	"sync"

	pluginv1 "github.com/Silo-Server/silo-plugin-sdk/pkg/pluginproto/silo/plugin/v1"

	"github.com/Moonfin-Client/Plugin/Silo/internal/store"
)

type Server struct {
	pluginv1.UnimplementedHttpRoutesServer

	version string
	kv      store.KV
	catalog Catalog
	mux     *http.ServeMux
	ext     external

	// mu serializes every read-modify-write of stored settings.
	mu sync.Mutex

	// native holds what Silo kept from its old plugin form (legacy_form.go).
	nativeMu sync.RWMutex
	native   map[string]M
}

func NewServer(version string, kv store.KV, catalog Catalog) *Server {
	if version == "" {
		version = "0.0.0-dev"
	}
	s := &Server{version: version, kv: kv, catalog: catalog, mux: http.NewServeMux(), ext: newExternal()}
	s.routes()
	return s
}

func (s *Server) routes() {
	s.mux.HandleFunc("GET /Moonfin/Ping", s.user(s.handlePing))
	s.mux.HandleFunc("GET /Moonfin/Silo/Whoami", s.user(s.handleWhoami))
	s.mux.HandleFunc("GET /{$}", s.user(s.handleHome))
	s.settingsRoutes()
	s.externalRoutes()
	s.messageRoutes()
	s.adminRoutes()
	s.themeRoutes()
}

// Handle is the http_routes.v1 entry point.
func (s *Server) Handle(ctx context.Context, req *pluginv1.HandleHTTPRequest) (*pluginv1.HandleHTTPResponse, error) {
	query := url.Values{}
	for k, v := range req.GetQuery().AsMap() {
		query.Set(k, stringify(v))
	}
	target := (&url.URL{Path: req.GetPath(), RawQuery: query.Encode()}).RequestURI()
	r := httptest.NewRequestWithContext(ctx, req.GetMethod(), target, bytes.NewReader(req.GetBody()))
	r.Header = http.Header{}
	for k, v := range req.GetHeaders() {
		r.Header.Set(k, v)
	}
	w := httptest.NewRecorder()
	s.mux.ServeHTTP(w, r)

	headers := map[string]string{}
	for k, v := range w.Header() {
		if len(v) > 0 {
			headers[k] = v[0]
		}
	}
	return &pluginv1.HandleHTTPResponse{StatusCode: int32(w.Code), Headers: headers, Body: w.Body.Bytes()}, nil
}

func stringify(v any) string {
	switch t := v.(type) {
	case string:
		return t
	case nil:
		return ""
	default:
		b, _ := json.Marshal(t)
		return string(b)
	}
}

// Identity is who Silo says is calling. The host stamps these headers itself
// and never forwards client-sent X-Silo-* headers, so they can be trusted.
type Identity struct {
	UserID int
	// Admin is Silo's acting-admin decision: an admin account through its
	// primary profile, the rule Silo's own admin API applies.
	Admin       bool
	UserName    string
	ProfileID   string
	ProfileName string
}

func identityFrom(r *http.Request) (Identity, bool) {
	id, err := strconv.Atoi(r.Header.Get("X-Silo-User-Id"))
	if err != nil || id <= 0 {
		return Identity{}, false
	}
	return Identity{
		UserID:      id,
		Admin:       r.Header.Get("X-Silo-User-Role") == "admin",
		UserName:    r.Header.Get("X-Silo-User-Name"),
		ProfileID:   r.Header.Get("X-Silo-Profile-Id"),
		ProfileName: r.Header.Get("X-Silo-Profile-Name"),
	}, true
}

type identityKey struct{}

func callerOf(r *http.Request) Identity {
	id, _ := r.Context().Value(identityKey{}).(Identity)
	return id
}

// user requires a signed-in caller. Silo already refuses anonymous callers on
// routes declared "authenticated", this is the second check.
func (s *Server) user(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, ok := identityFrom(r)
		if !ok {
			writeError(w, http.StatusUnauthorized, "Sign in to Silo first.")
			return
		}
		next(w, r.WithContext(context.WithValue(r.Context(), identityKey{}, id)))
	}
}

func (s *Server) admin(next http.HandlerFunc) http.HandlerFunc {
	return s.user(func(w http.ResponseWriter, r *http.Request) {
		if !callerOf(r).Admin {
			writeError(w, http.StatusForbidden, "Only a Silo admin can do this.")
			return
		}
		next(w, r)
	})
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]any{"Error": message})
}

func logError(r *http.Request, msg string, err error) {
	slog.ErrorContext(r.Context(), msg, "path", r.URL.Path, "error", err)
}
