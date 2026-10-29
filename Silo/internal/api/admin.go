package api

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"html"
	"mime"
	"net/http"
	"net/url"
	"path"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/Moonfin-Client/Plugin/Silo/internal/settings"
	"github.com/Moonfin-Client/Plugin/Silo/web"
)

// The admin page is the Jellyfin plugin's configPage.html, served as is with
// web/host.js standing in for the Jellyfin dashboard around it.

const keyConfig = "config"

func (s *Server) adminRoutes() {
	// Go's built-in table has no woff2, and the plugin may run without /etc/mime.types.
	_ = mime.AddExtensionType(".woff2", "font/woff2")
	m := s.mux
	m.HandleFunc("GET /admin", s.admin(s.handleAdminPage))
	m.HandleFunc("GET /admin-assets/{file...}", s.admin(s.handleAdminAsset))
	m.HandleFunc("GET /Moonfin/Assets/icon.png", s.user(s.handleIcon))
	m.HandleFunc("GET /Moonfin/Admin/Configuration", s.admin(s.handleGetConfiguration))
	m.HandleFunc("POST /Moonfin/Admin/Configuration", s.admin(s.handlePostConfiguration))
	m.HandleFunc("GET /Moonfin/Admin/Users", s.admin(s.handleAdminUsers))
	m.HandleFunc("DELETE /Moonfin/Admin/Users/{userId}", s.admin(s.handleDeleteUser))
	m.HandleFunc("GET /Moonfin/MdbList/KeyInfo", s.admin(s.handleMdbListKeyInfo))
	m.HandleFunc("POST /Moonfin/MdbList/ClearCache", s.admin(s.handleMdbListClearCache))
}

var adminPageBody = sync.OnceValue(func() string {
	raw, err := web.Files.ReadFile("configPage.html")
	if err != nil {
		panic(err)
	}
	page := string(raw)
	start, end := strings.Index(page, "<body>"), strings.LastIndex(page, "</body>")
	if start < 0 || end < start {
		panic("configPage.html has no body")
	}
	return page[start+len("<body>") : end]
})

func (s *Server) handleAdminPage(w http.ResponseWriter, r *http.Request) {
	v := url.QueryEscape(s.version)
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	fmt.Fprintf(w, `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Moonbase</title>
<link rel="stylesheet" href="admin-assets/host.css?v=%[1]s">
<script src="admin-assets/host.js?v=%[1]s"></script>
</head>
<body>
<div class="moonbaseHostBar"><a href="/admin/plugins" title="Back to Silo" aria-label="Back to Silo"><span class="material-icons" aria-hidden="true">arrow_back</span></a><span class="moonbaseHostVersion">Moonbase %[2]s for Silo</span></div>
<div class="moonbaseHostNotices"></div>
%[3]s
</body>
</html>`, v, html.EscapeString(s.version), adminPageBody())
}

func serveEmbedded(w http.ResponseWriter, r *http.Request, name, cacheControl string) {
	data, err := web.Files.ReadFile(name)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", mime.TypeByExtension(path.Ext(name)))
	w.Header().Set("Cache-Control", cacheControl)
	_, _ = w.Write(data)
}

func (s *Server) handleAdminAsset(w http.ResponseWriter, r *http.Request) {
	name := r.PathValue("file")
	if name == "configPage.html" || strings.Contains(name, "..") {
		http.NotFound(w, r)
		return
	}
	serveEmbedded(w, r, name, "public, max-age=86400")
}

func (s *Server) handleIcon(w http.ResponseWriter, r *http.Request) {
	serveEmbedded(w, r, "icon.png", "public, max-age=86400")
}

// Fields the page reads from the configuration that Moonbase keeps elsewhere
// or works out, so a save never stores them.
var derivedConfigKeys = []string{"Messages", "UploadedThemes", "HasServiceAccount", "PushEnabled"}

func (s *Server) handleGetConfiguration(w http.ResponseWriter, r *http.Request) {
	out, err := s.fullConfig(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	messages, err := s.loadMessages(r)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	themes, err := s.loadThemes(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	out["Messages"] = messages
	out["UploadedThemes"] = sortedThemes(themes.Items)
	out["HasServiceAccount"] = false
	out["PushEnabled"] = false
	writeJSON(w, http.StatusOK, out)
}

// handlePostConfiguration replaces the whole configuration, as Jellyfin's
// plugin configuration endpoint does. The page sends back everything it
// read, the old form's values included, so they're kept from here on.
func (s *Server) handlePostConfiguration(w http.ResponseWriter, r *http.Request) {
	var body M
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body == nil {
		writeError(w, http.StatusBadRequest, "A JSON object of plugin settings is required")
		return
	}
	for _, k := range derivedConfigKeys {
		delete(body, k)
	}
	if d, ok := body["DefaultUserSettings"].(M); ok {
		body["DefaultUserSettings"] = settings.Clean(d)
	}
	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	body[keyFormMoved] = true
	if err := s.saveJSON(ctx, keyConfig, body); err != nil {
		storageFailed(w, r, err)
		return
	}
	if err := s.followChangedDefaults(ctx); err != nil {
		logError(r, "apply changed defaults to users", err)
	}
	w.WriteHeader(http.StatusNoContent)
}

// keyDefaultsSeen holds the admin defaults users were last moved onto.
const keyDefaultsSeen = "s/defaults-seen"

// followChangedDefaults moves users onto each default the admin changed
// since the last run (the caller holds s.mu): a user's stored value gives
// way to the new default when it equals the old one, or when there was no
// default before (apps store every value they have, so a value from then
// is the app's own default, not a choice). Values a user picked against an
// earlier default are kept. The first run only records the defaults.
func (s *Server) followChangedDefaults(ctx context.Context) error {
	after := s.config(ctx).Defaults
	var before M
	found, err := s.loadJSON(ctx, keyDefaultsSeen, &before)
	if err != nil {
		return err
	}
	if !found {
		return s.saveJSON(ctx, keyDefaultsSeen, nonNil(after))
	}
	changed := M{}
	for k, v := range after {
		if old, ok := before[k]; !ok || !settings.SameValue(old, v) {
			changed[k] = v
		}
	}
	if len(changed) == 0 {
		return nil
	}
	var index []indexEntry
	if _, err := s.loadJSON(ctx, keyIndex, &index); err != nil {
		return err
	}
	var errs []error
	for _, e := range index {
		env, err := s.loadEnvelope(ctx, e.identity())
		if err != nil || env == nil {
			errs = append(errs, err)
			continue
		}
		follows := func(k string, v any) bool {
			if _, ok := changed[k]; !ok {
				return false
			}
			old, had := before[k]
			return !had || settings.SameValue(old, v)
		}
		if settings.Forget(env, follows) {
			env["lastUpdated"] = time.Now().UnixMilli()
			env["lastUpdatedBy"] = "admin-defaults-changed"
			errs = append(errs, s.saveJSON(ctx, e.key(), env))
		}
	}
	// Recorded even after a failure, so a later save doesn't take a user's
	// value from this round for one from before it.
	errs = append(errs, s.saveJSON(ctx, keyDefaultsSeen, nonNil(after)))
	return errors.Join(errs...)
}

func nonNil(m M) M {
	if m == nil {
		return M{}
	}
	return m
}

// handleDeleteUser removes every profile's settings that a Silo account has
// stored, for an account that was deleted from Silo or wants a fresh start.
// Silo doesn't tell plugins when an account goes, so the admin does this.
func (s *Server) handleDeleteUser(w http.ResponseWriter, r *http.Request) {
	userID, err := strconv.Atoi(r.PathValue("userId"))
	if err != nil || userID <= 0 {
		writeJSON(w, http.StatusBadRequest, M{"error": "A valid userId is required"})
		return
	}
	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	var index []indexEntry
	if _, err := s.loadJSON(ctx, keyIndex, &index); err != nil {
		storageFailed(w, r, err)
		return
	}
	kept := index[:0:0]
	removed := 0
	for _, e := range index {
		if e.UserID != userID {
			kept = append(kept, e)
			continue
		}
		if err := s.saveJSON(ctx, e.key(), nil); err != nil {
			storageFailed(w, r, err)
			return
		}
		removed++
	}
	if removed > 0 {
		if err := s.saveJSON(ctx, keyIndex, kept); err != nil {
			storageFailed(w, r, err)
			return
		}
	}
	writeJSON(w, http.StatusOK, M{"success": true, "profilesRemoved": removed})
}

// handleAdminUsers lists, in the shape Jellyfin's /Users has, the Silo
// accounts that have synced settings. Silo doesn't let plugins list accounts.
func (s *Server) handleAdminUsers(w http.ResponseWriter, r *http.Request) {
	var index []indexEntry
	if _, err := s.loadJSON(r.Context(), keyIndex, &index); err != nil {
		storageFailed(w, r, err)
		return
	}
	users := []M{}
	seen := map[int]bool{}
	for _, e := range index {
		if seen[e.UserID] {
			continue
		}
		seen[e.UserID] = true
		name := e.UserName
		if name == "" {
			name = "Account " + strconv.Itoa(e.UserID)
		}
		users = append(users, M{"Id": strconv.Itoa(e.UserID), "Name": name, "Policy": M{"IsAdministrator": e.Admin}})
	}
	writeJSON(w, http.StatusOK, users)
}

func (s *Server) handleMdbListKeyInfo(w http.ResponseWriter, r *http.Request) {
	key := strings.TrimSpace(r.URL.Query().Get("key"))
	if key == "" {
		key = s.config(r.Context()).MdblistAPIKey
	}
	if key == "" {
		writeJSON(w, http.StatusOK, M{"success": false, "error": "No MDBList API key configured.", "isSupporter": false})
		return
	}
	var info M
	status, err := s.getJSON(r.Context(), s.ext.mdblistBase+"/user?apikey="+url.QueryEscape(key), "", &info)
	switch {
	case err != nil:
		writeJSON(w, http.StatusOK, M{"success": false, "error": fmt.Sprintf("Failed to reach MDBList: %v", err), "isSupporter": false})
	case status == http.StatusTooManyRequests:
		writeJSON(w, http.StatusOK, M{"success": false, "error": "MDBList rate limit reached. Try again later.", "isSupporter": false})
	case status/100 != 2:
		writeJSON(w, http.StatusOK, M{"success": false, "error": fmt.Sprintf("MDBList rejected the key (status %d).", status), "isSupporter": false})
	case str(info["username"]) == "":
		writeJSON(w, http.StatusOK, M{"success": false, "error": "Unexpected response from MDBList.", "isSupporter": false})
	default:
		supporter, _ := info["is_supporter"].(bool)
		writeJSON(w, http.StatusOK, compact(M{"success": true, "username": info["username"], "plan": info["plan"],
			"isSupporter": supporter, "apiRequests": info["api_requests"], "apiRequestsCount": info["api_requests_count"],
			"rateLimitRemaining": info["rate_limit_remaining"]}))
	}
}

func (s *Server) handleMdbListClearCache(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, M{"success": true, "removed": s.ext.cache.clearPrefix("mdblist")})
}
