package api

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"slices"
	"strings"
	"testing"

	pluginv1 "github.com/Silo-Server/silo-plugin-sdk/pkg/pluginproto/silo/plugin/v1"

	"github.com/Moonfin-Client/Plugin/Silo/internal/store"
)

type fakeCatalog struct{}

func (fakeCatalog) Libraries(context.Context, int) ([]Library, error) {
	return []Library{{ID: "1", Name: "Movies", MediaType: "movie"}}, nil
}

// caller is the identity Silo's proxy stamps on a request. The zero value is
// an anonymous caller. admin is Silo's acting-admin decision, so an admin
// account's child profile reaches the plugin as a user.
type caller struct {
	user      int
	admin     bool
	profileID string
	profile   string
	name      string
}

var (
	anon      = caller{}
	adminMain = caller{user: 2, admin: true, profileID: "20", profile: "test"}
	adminKid  = caller{user: 2, profileID: "21", profile: "Kid"}
	userMain  = caller{user: 3, profileID: "30", profile: "Main", name: "pat"}
	userKids  = caller{user: 3, profileID: "31", profile: "Kids", name: "pat"}
	other     = caller{user: 4, profileID: "40", profile: "Main"}
)

func (c caller) headers() map[string]string {
	h := map[string]string{"Content-Type": "application/json"}
	if c.user == 0 {
		return h
	}
	h["X-Silo-User-Id"] = fmt.Sprint(c.user)
	h["X-Silo-User-Role"] = "user"
	if c.admin {
		h["X-Silo-User-Role"] = "admin"
	}
	if c.profileID != "" {
		h["X-Silo-Profile-Id"] = c.profileID
	}
	if c.profile != "" {
		h["X-Silo-Profile-Name"] = c.profile
	}
	if c.name != "" {
		h["X-Silo-User-Name"] = c.name
	}
	return h
}

func newTestServer() *Server {
	s := NewServer("1.2.3", store.NewMemKV(), fakeCatalog{})
	// Stands in for Silo's config store: what the page writes comes back the
	// way Configure would hand it over after a restart.
	s.setConfigEntry = func(_ context.Context, key string, value map[string]any) error {
		next := map[string]M{}
		for k, v := range s.nativeConfig() {
			next[k] = v
		}
		next[key] = value
		s.setNative(next)
		return nil
	}
	return s
}

func call(t *testing.T, s *Server, c caller, method, path, body string, query map[string]any) (int, map[string]any) {
	t.Helper()
	resp, err := s.Handle(context.Background(), &pluginv1.HandleHTTPRequest{
		Method: method, Path: path, Headers: c.headers(), Body: []byte(body), Query: mustStruct(t, query),
	})
	if err != nil {
		t.Fatal(err)
	}
	var out map[string]any
	if len(resp.GetBody()) > 0 && strings.HasPrefix(resp.GetHeaders()["Content-Type"], "application/json") {
		if err := json.Unmarshal(resp.GetBody(), &out); err != nil {
			t.Fatalf("%s %s: bad JSON %q", method, path, resp.GetBody())
		}
	}
	return int(resp.GetStatusCode()), out
}

func TestAuthorizationMatrix(t *testing.T) {
	s := newTestServer()
	cases := []struct {
		c            caller
		method, path string
		want         int
	}{
		{anon, "GET", "/Moonfin/Ping", 401},
		{userKids, "GET", "/Moonfin/Ping", 200},
		{anon, "GET", "/Moonfin/Settings/Resolved/tv", 401},
		{userKids, "GET", "/Moonfin/Settings/Resolved/tv", 200},
		{userKids, "GET", "/Moonfin/Settings/Resolved/phone", 400},
		{userMain, "GET", "/Moonfin/Admin/Configuration", 403},
		{adminKid, "GET", "/Moonfin/Admin/Configuration", 403},
		{adminMain, "GET", "/Moonfin/Admin/Configuration", 200},
		{userMain, "GET", "/admin", 403},
		{adminMain, "GET", "/admin", 200},
		{userMain, "GET", "/Moonfin/Admin/Users", 403},
		{userMain, "POST", "/Moonfin/Admin/PushDefaults", 403},
		{adminMain, "GET", "/Moonfin/Settings/Stream", 501},
		{userMain, "GET", "/Moonfin/Nope", 404},
	}
	for _, tc := range cases {
		if got, _ := call(t, s, tc.c, tc.method, tc.path, "", nil); got != tc.want {
			t.Errorf("%+v %s %s = %d, want %d", tc.c, tc.method, tc.path, got, tc.want)
		}
	}
}

func TestSettingsStayWithTheirProfile(t *testing.T) {
	s := newTestServer()
	if code, _ := call(t, s, userKids, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"posterSize":"small"},"clientId":"x"}`, nil); code != 200 {
		t.Fatalf("save = %d", code)
	}
	if _, got := call(t, s, userKids, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); got["posterSize"] != "small" {
		t.Fatalf("kids profile lost its setting: %v", got)
	}
	for _, c := range []caller{userMain, other, adminMain} {
		if _, got := call(t, s, c, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); got["posterSize"] != nil {
			t.Fatalf("%+v sees another profile's setting: %v", c, got)
		}
		if code, _ := call(t, s, c, "GET", "/Moonfin/Settings", "", nil); code != 404 {
			t.Fatalf("%+v reads another profile's envelope: %d", c, code)
		}
	}
	renamed := userKids
	renamed.profile = "Little ones"
	if _, got := call(t, s, renamed, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); got["posterSize"] != "small" {
		t.Fatalf("a renamed profile lost its settings: %v", got)
	}
	if code, _ := call(t, s, userKids, "DELETE", "/Moonfin/Settings", "", nil); code != 200 {
		t.Fatal("delete failed")
	}
	if code, _ := call(t, s, userKids, "GET", "/Moonfin/Settings", "", nil); code != 404 {
		t.Fatal("settings survived delete")
	}
}

func TestAdminDefaultsAndPush(t *testing.T) {
	s := newTestServer()
	call(t, s, userMain, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil)
	saveConfig(t, s, map[string]any{"DefaultUserSettings": map[string]any{"b": 2, "c": nil}})
	if _, got := call(t, s, other, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); got["b"] != 2.0 {
		t.Fatalf("a new profile must get the defaults: %v", got)
	}
	if _, got := call(t, s, userKids, "GET", "/Moonfin/Ping", "", nil); got["defaultSettings"].(map[string]any)["b"] != 2.0 {
		t.Fatalf("ping must carry the defaults: %v", got)
	}

	code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults", "", map[string]any{"overwrite": "true"})
	if code != 200 || got["usersAffected"] != 1.0 {
		t.Fatalf("push = %d %v", code, got)
	}
	if _, env := call(t, s, userMain, "GET", "/Moonfin/Settings", "", nil); env["tv"] != nil || len(env["global"].(map[string]any)) != 0 {
		t.Fatalf("overwrite must clear the user's settings: %v", env)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); got["b"] != 2.0 {
		t.Fatalf("a reset user must get the defaults: %v", got)
	}
	if _, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults", "", map[string]any{"overwrite": "True"}); got["overwrite"] != true {
		t.Fatalf("overwrite must ignore case: %v", got)
	}
}

func TestDefaultsReachUsersWhoNeverChangedThem(t *testing.T) {
	s := newTestServer()
	saveConfig(t, s, map[string]any{"DefaultUserSettings": map[string]any{"style": "classic"}})
	// Apps send every setting they hold, the ones from the defaults included.
	call(t, s, userMain, "POST", "/Moonfin/Settings/Profile/mobile", `{"profile":{"style":"classic","thumbs":false}}`, nil)
	call(t, s, other, "POST", "/Moonfin/Settings/Profile/mobile", `{"profile":{"style":"modern","thumbs":false}}`, nil)
	if _, env := call(t, s, userMain, "GET", "/Moonfin/Settings", "", nil); env["mobile"].(map[string]any)["style"] != nil {
		t.Fatalf("a value equal to the default must not be stored: %v", env)
	}

	saveConfig(t, s, map[string]any{"DefaultUserSettings": map[string]any{"style": "spotlight", "thumbs": true}})
	resolved := func(c caller) map[string]any {
		_, got := call(t, s, c, "GET", "/Moonfin/Settings/Resolved/mobile", "", nil)
		return got
	}
	if got := resolved(userMain); got["style"] != "spotlight" || got["thumbs"] != true {
		t.Fatalf("a user on the defaults must follow them, and a new default replaces the app's own: %v", got)
	}
	if got := resolved(other); got["style"] != "modern" || got["thumbs"] != true {
		t.Fatalf("a value picked against the old default must stay: %v", got)
	}

	// Silo's own form saves restart the plugin with the new values.
	native := toNative(map[string]any{"DefaultUserSettings": map[string]any{"detailScreenStyle": "classic"}})
	call(t, s, userMain, "POST", "/Moonfin/Settings/Profile/mobile", `{"profile":{"detailScreenStyle":"modern"}}`, nil)
	entries := []*pluginv1.ConfigEntry{}
	for k, v := range native {
		entries = append(entries, &pluginv1.ConfigEntry{Key: k, Value: mustStruct(t, v)})
	}
	if err := s.Configure(context.Background(), entries); err != nil {
		t.Fatal(err)
	}
	if got := resolved(userMain); got["detailScreenStyle"] != "classic" {
		t.Fatalf("a default first set in Silo's form must reach users: %v", got)
	}
}

func TestDeleteUserRemovesEveryProfile(t *testing.T) {
	s := newTestServer()
	for _, c := range []caller{userMain, userKids, other} {
		call(t, s, c, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil)
	}
	if code, _ := call(t, s, userMain, "DELETE", "/Moonfin/Admin/Users/3", "", nil); code != http.StatusForbidden {
		t.Fatalf("only an admin may remove a user's data, got %d", code)
	}
	if code, got := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Users/3", "", nil); code != 200 || got["profilesRemoved"] != 2.0 {
		t.Fatalf("delete = %d %v", code, got)
	}
	for _, c := range []caller{userMain, userKids} {
		if code, _ := call(t, s, c, "GET", "/Moonfin/Settings", "", nil); code != http.StatusNotFound {
			t.Fatalf("%v kept settings", c)
		}
	}
	if code, _ := call(t, s, other, "GET", "/Moonfin/Settings", "", nil); code != 200 {
		t.Fatal("another account's settings must stay")
	}
	var index []indexEntry
	if _, err := s.loadJSON(context.Background(), keyIndex, &index); err != nil || len(index) != 1 || index[0].UserID != 4 {
		t.Fatalf("index = %v %v", index, err)
	}
}

func TestStorageLimitLeavesNoPhantomProfile(t *testing.T) {
	s := newTestServer()
	saveConfig(t, s, map[string]any{"DefaultUserSettings": map[string]any{"b": 2}})
	refused := 0
	for i := range store.MaxKeys {
		if code, _ := call(t, s, caller{user: 100 + i, profile: "P"}, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil); code != 200 {
			refused = 100 + i
			break
		}
	}
	if refused == 0 {
		t.Fatal("the key limit was never reached")
	}
	var index []indexEntry
	if _, err := s.loadJSON(context.Background(), keyIndex, &index); err != nil {
		t.Fatal(err)
	}
	if slices.ContainsFunc(index, func(e indexEntry) bool { return e.UserID == refused }) {
		t.Fatal("a refused save must not leave its profile in the index")
	}
	for _, q := range []map[string]any{nil, {"overwrite": "true"}} {
		if code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults", "", q); code != 200 {
			t.Fatalf("push %v at the key limit = %d %v", q, code, got)
		}
	}
}

func TestWholeEnvelopeSaveKeepsSyncOn(t *testing.T) {
	s := newTestServer()
	call(t, s, userMain, "POST", "/Moonfin/Settings", `{"settings":{"global":{"a":1}},"mergeMode":"replace"}`, nil)
	if _, env := call(t, s, userMain, "GET", "/Moonfin/Settings", "", nil); env["syncEnabled"] != true {
		t.Fatalf("a replace without syncEnabled must store it on: %v", env)
	}
	call(t, s, userMain, "POST", "/Moonfin/Settings", `{"settings":{"syncEnabled":false}}`, nil)
	if _, env := call(t, s, userMain, "GET", "/Moonfin/Settings", "", nil); env["syncEnabled"] != false {
		t.Fatalf("a merge must keep the sent syncEnabled: %v", env)
	}
}

func TestDetailsBlurRoundTrip(t *testing.T) {
	s := newTestServer()
	code, got := call(t, s, userMain, "POST", "/Moonfin/Settings/detailsScreenBlur/tv", `{"detailsScreenBlur":"55"}`, nil)
	if code != 200 || got["DetailsScreenBlur"] != "40" {
		t.Fatalf("blur save = %d %v", code, got)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Settings/detailsScreenBlur/tv", "", nil); got["DetailsScreenBlur"] != "40" {
		t.Fatalf("blur read = %v", got)
	}
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Settings/detailsScreenBlur", `{"detailsScreenBlur":"lots"}`, nil); code != 400 {
		t.Fatalf("non-numeric blur = %d", code)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Settings/detailsScreenOpacity", "", nil); got["DetailsScreenOpacity"] != 90.0 {
		t.Fatalf("default opacity = %v", got)
	}
}

func TestSyncDisabledTurnsSettingsOff(t *testing.T) {
	s := newTestServer()
	saveConfig(t, s, map[string]any{"EnableSettingsSync": false, "TmdbApiKey": " k "})
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); code != http.StatusServiceUnavailable {
		t.Fatalf("resolved = %d", code)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); got["settingsSyncEnabled"] != false || got["tmdbAvailable"] != true {
		t.Fatalf("ping = %v", got)
	}
}

func TestMessagesReachTheirAudience(t *testing.T) {
	s := newTestServer()
	post := func(body string) map[string]any {
		t.Helper()
		code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/Messages", body, nil)
		if code != 200 {
			t.Fatalf("post %s = %d %v", body, code, got)
		}
		return got["item"].(map[string]any)
	}
	post(`{"title":"For all","color":"purple","actionUrl":"javascript:alert(1)","actionLabel":"Go"}`)
	post(`{"title":"For admins","audience":"admins"}`)
	post(`{"title":"For user 3","audience":"users","targetUserIds":["3","x","3"]}`)
	post(`{"title":"Over","endUtc":"2000-01-01T00:00:00Z"}`)
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Admin/Messages", `{"title":"x"}`, nil); code != 403 {
		t.Fatal("a user must not post messages")
	}

	titles := func(c caller) []string {
		_, got := call(t, s, c, "GET", "/Moonfin/Messages", "", nil)
		var out []string
		for _, item := range got["items"].([]any) {
			out = append(out, item.(map[string]any)["Title"].(string))
		}
		return out
	}
	if got := strings.Join(titles(userKids), ","); got != "For user 3,For all" {
		t.Errorf("user 3 sees %q", got)
	}
	if got := strings.Join(titles(other), ","); got != "For all" {
		t.Errorf("user 4 sees %q", got)
	}
	if got := strings.Join(titles(adminKid), ","); got != "For all" {
		t.Errorf("an admin's child profile sees %q", got)
	}
	if got := strings.Join(titles(adminMain), ","); got != "For admins,For all" {
		t.Errorf("admin sees %q", got)
	}

	_, all := call(t, s, adminMain, "GET", "/Moonfin/Admin/Messages", "", nil)
	first := all["items"].([]any)[2].(map[string]any)
	if first["Color"] != "white" || first["ActionUrl"] != nil || first["ActionLabel"] != nil {
		t.Errorf("sanitize let through %v", first)
	}
	if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Messages/"+first["Id"].(string), "", nil); code != 200 {
		t.Fatal("delete failed")
	}
	if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Messages/nope", "", nil); code != 404 {
		t.Fatal("deleting a missing message must 404")
	}
}

func TestDeletingNothingUsesNoStorage(t *testing.T) {
	kv := store.NewMemKV()
	s := NewServer("1", kv, fakeCatalog{})
	for i := range 5 {
		c := caller{user: 10 + i, profile: "P"}
		if code, _ := call(t, s, c, "DELETE", "/Moonfin/Settings", "", nil); code != 200 {
			t.Fatalf("delete = %d", code)
		}
	}
	if _, found, _ := kv.Get(context.Background(), settingsKey(Identity{UserID: 10, ProfileName: "P"})); found {
		t.Fatal("deleting settings that were never stored must not write a key")
	}
}

// saveConfig saves plugin settings the way the admin page does.
func saveConfig(t *testing.T, s *Server, config map[string]any) {
	t.Helper()
	body, _ := json.Marshal(config)
	if code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/Configuration", string(body), nil); code != http.StatusNoContent {
		t.Fatalf("save config = %d %v", code, got)
	}
}

func TestAdminConfigurationRoundTrip(t *testing.T) {
	s := newTestServer()
	call(t, s, adminMain, "POST", "/Moonfin/Admin/Messages", `{"title":"Hi"}`, nil)
	_, got := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil)
	if got["EnableSettingsSync"] != true || got["MdblistOfficialListsMaxItems"] != 250.0 || len(got["Messages"].([]any)) != 1 {
		t.Fatalf("a fresh install must answer Jellyfin's defaults and the stored messages: %v", got)
	}

	got["TmdbApiKey"] = "k"
	got["DefaultUserSettings"] = map[string]any{"NavbarPosition": "bottom", "x": nil}
	got["Messages"] = []any{}
	saveConfig(t, s, got)

	_, again := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil)
	if again["TmdbApiKey"] != "k" || len(again["Messages"].([]any)) != 1 {
		t.Fatalf("saving the page must keep its fields and leave messages alone: %v", again)
	}
	if d := again["DefaultUserSettings"].(map[string]any); d["navbarPosition"] != "bottom" || len(d) != 1 {
		t.Fatalf("defaults must be stored cleaned: %v", d)
	}
	if _, ping := call(t, s, userKids, "GET", "/Moonfin/Ping", "", nil); ping["tmdbAvailable"] != true {
		t.Fatal("ping must read the saved key")
	}
}

func TestAdminUsersComeFromSyncedProfiles(t *testing.T) {
	s := newTestServer()
	for _, c := range []caller{userMain, userKids, adminMain} {
		call(t, s, c, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil)
	}
	resp, err := s.Handle(context.Background(), &pluginv1.HandleHTTPRequest{Method: "GET", Path: "/Moonfin/Admin/Users", Headers: adminMain.headers()})
	if err != nil {
		t.Fatal(err)
	}
	var users []map[string]any
	if err := json.Unmarshal(resp.GetBody(), &users); err != nil {
		t.Fatal(err)
	}
	if len(users) != 2 || users[0]["Id"] != "3" || users[0]["Name"] != "pat" || users[1]["Name"] != "Account 2" ||
		users[1]["Policy"].(map[string]any)["IsAdministrator"] != true {
		t.Fatalf("users = %v", users)
	}
}

func TestAdminPageServesTheJellyfinPage(t *testing.T) {
	s := newTestServer()
	resp, _ := s.Handle(context.Background(), &pluginv1.HandleHTTPRequest{Method: "GET", Path: "/admin", Headers: adminMain.headers()})
	page := string(resp.GetBody())
	for _, want := range []string{`<script src="admin-assets/host.js?v=1.2.3">`, `id="MoonfinConfigPage"`, "MoonfinConfig.pluginUniqueId"} {
		if !strings.Contains(page, want) {
			t.Errorf("page is missing %q", want)
		}
	}
	for _, asset := range []string{"/admin-assets/host.js", "/admin-assets/host.css", "/admin-assets/fonts/MaterialIcons-Regular.woff2", "/Moonfin/Assets/icon.png"} {
		resp, _ := s.Handle(context.Background(), &pluginv1.HandleHTTPRequest{Method: "GET", Path: asset, Headers: adminMain.headers()})
		if resp.GetStatusCode() != 200 || resp.GetHeaders()["Content-Type"] == "" || len(resp.GetBody()) == 0 {
			t.Errorf("%s = %d %q", asset, resp.GetStatusCode(), resp.GetHeaders()["Content-Type"])
		}
	}
	resp, _ = s.Handle(context.Background(), &pluginv1.HandleHTTPRequest{Method: "GET", Path: "/admin-assets/configPage.html", Headers: adminMain.headers()})
	if resp.GetStatusCode() != 404 {
		t.Error("the raw page must not be served as an asset")
	}
}

func TestNativeFormValuesReachTheRoutes(t *testing.T) {
	s := newTestServer()
	entries := byCard(t, map[string]any{
		"EnableSettingsSync": true, "TmdbApiKey": "native-key", "SeerrEnabled": true,
		"navbarPosition": "bottom", "use24HourClock": "true", "confirmExit": "",
		"mediaBarItemCount": "10", "browsingBlur": 12.0, "themeMusicVolume": 40.0,
		"mediaSegmentActionIntro": "skip", "mediaSegmentActionOutro": "askToSkip",
		"bottomNavbarTab1": "home", "bottomNavbarTab2": "", "bottomNavbarTab3": "home",
		"seasonalRowShownHolidays": []any{"christmas"},
	})
	var config []*pluginv1.ConfigEntry
	for k, v := range entries {
		config = append(config, &pluginv1.ConfigEntry{Key: k, Value: mustStruct(t, v)})
	}
	if err := s.Configure(context.Background(), config); err != nil {
		t.Fatal(err)
	}
	_, got := call(t, s, userKids, "GET", "/Moonfin/Settings/Resolved/tv", "", nil)
	want := map[string]any{"navbarPosition": "bottom", "use24HourClock": true, "mediaBarItemCount": 10.0,
		"browsingBlur": "12", "themeMusicVolume": 40.0, "tmdbApiKey": "native-key",
		"mediaSegmentActions": "intro:skip,outro:askToSkip"}
	for k, v := range want {
		if got[k] != v {
			t.Errorf("%s = %#v, want %#v", k, got[k], v)
		}
	}
	if _, set := got["confirmExit"]; set {
		t.Error("Not set must stay unset")
	}
	if tabs, _ := got["bottomNavbarTabs"].([]any); len(tabs) != 1 || tabs[0] != "home" {
		t.Errorf("bottom tabs must skip blanks and repeats: %v", got["bottomNavbarTabs"])
	}
	if hidden, _ := got["seasonalRowHiddenHolidays"].([]any); len(hidden) == 0 || slices.Contains(hidden, any("christmas")) {
		t.Errorf("hidden holidays must be every holiday not ticked: %v", got["seasonalRowHiddenHolidays"])
	}
}

// byCard puts form values on the cards the generated table assigns them to.
func byCard(t *testing.T, values map[string]any) map[string]map[string]any {
	t.Helper()
	out := map[string]map[string]any{}
	for k, v := range values {
		i := slices.IndexFunc(formFields, func(f formField) bool { return f.Field == k })
		if i < 0 {
			t.Fatalf("%s isn't on the form", k)
		}
		if out[formFields[i].Entry] == nil {
			out[formFields[i].Entry] = map[string]any{}
		}
		out[formFields[i].Entry][k] = v
	}
	return out
}

// nativeValue reads one form field from what the plugin holds for Silo's form.
func nativeValue(s *Server, field string) any {
	i := slices.IndexFunc(formFields, func(f formField) bool { return f.Field == field })
	return s.nativeConfig()[formFields[i].Entry][field]
}

func TestPageSaveWritesTheNativeForm(t *testing.T) {
	s := newTestServer()
	_, cfg := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil)
	cfg["MdblistApiKey"] = "page-key"
	cfg["EnableSettingsSync"] = false
	cfg["DefaultUserSettings"] = map[string]any{"navbarPosition": "left", "use24HourClock": false,
		"mediaBarItemCount": 15, "homeSections": []any{map[string]any{"type": "resume"}},
		"mediaSegmentActions": "recap:skip", "bottomNavbarTabs": []any{"home", "search"},
		"seasonalRowHiddenHolidays": []any{"halloween"}}
	saveConfig(t, s, cfg)

	for field, want := range map[string]any{"MdblistApiKey": "page-key", "EnableSettingsSync": false,
		"navbarPosition": "left", "use24HourClock": "false", "mediaBarItemCount": "15", "confirmExit": "",
		"mediaSegmentActionRecap": "skip", "mediaSegmentActionIntro": "", "bottomNavbarTab2": "search", "bottomNavbarTab3": ""} {
		if got := nativeValue(s, field); got != want {
			t.Errorf("form %s = %#v, want %#v", field, got, want)
		}
	}
	if shown, _ := nativeValue(s, "seasonalRowShownHolidays").([]any); slices.Contains(shown, any("halloween")) || !slices.Contains(shown, any("christmas")) {
		t.Errorf("the form must tick every holiday but the hidden one: %v", shown)
	}

	var page M
	if _, err := s.loadJSON(context.Background(), keyConfig, &page); err != nil {
		t.Fatal(err)
	}
	if _, has := page["MdblistApiKey"]; has {
		t.Fatal("a form setting must not also be kept with the page settings")
	}
	if pd := page["DefaultUserSettings"].(M); len(pd) != 1 || pd["homeSections"] == nil {
		t.Fatalf("only page-only defaults may stay with the page settings: %v", pd)
	}

	_, again := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil)
	dd := again["DefaultUserSettings"].(map[string]any)
	if again["MdblistApiKey"] != "page-key" || dd["navbarPosition"] != "left" || dd["mediaBarItemCount"] != 15.0 || dd["homeSections"] == nil ||
		dd["mediaSegmentActions"] != "recap:skip" || len(dd["bottomNavbarTabs"].([]any)) != 2 {
		t.Fatalf("the page must read back both halves: %v", again)
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); code != 503 {
		t.Fatal("sync off from the page must turn sync off")
	}
}

func TestFormSchemaMatchesTheFieldTable(t *testing.T) {
	raw, err := os.ReadFile("../../config_schema.json")
	if err != nil {
		t.Fatal(err)
	}
	var schema []struct {
		Key       string `json:"key"`
		AdminForm struct {
			Fields []struct {
				Key string `json:"key"`
			} `json:"fields"`
		} `json:"admin_form"`
	}
	if err := json.Unmarshal(raw, &schema); err != nil {
		t.Fatal(err)
	}
	inSchema := map[string]bool{}
	for _, e := range schema {
		for _, f := range e.AdminForm.Fields {
			inSchema[e.Key+"/"+f.Key] = true
		}
	}
	for _, f := range formFields {
		if !inSchema[f.Entry+"/"+f.Field] {
			t.Errorf("%s/%s is in form_gen.go but not config_schema.json, run go run ./tools/genform", f.Entry, f.Field)
		}
	}
	if len(inSchema) != len(formFields) {
		t.Errorf("schema has %d fields, table has %d, run go run ./tools/genform", len(inSchema), len(formFields))
	}
}
