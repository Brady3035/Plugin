package api

import (
	"context"
	"math"
	"slices"
	"strconv"
	"strings"

	pluginv1 "github.com/Silo-Server/silo-plugin-sdk/pkg/pluginproto/silo/plugin/v1"
	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtime"

	"github.com/Moonfin-Client/Plugin/Silo/internal/store"
)

// Silo's native plugin form (config_schema.json, built from the Jellyfin
// page by tools/genform) holds every setting that has a plain control. Silo
// stores what an admin saves there and hands it over through Configure. The
// Moonbase page edits the same values and writes them back with
// SetGlobalConfigEntry, so both stay in step. Settings that need the page's
// own controls live in instance state under keyConfig.

type fieldKind int

const (
	kindBool      fieldKind = iota // a switch
	kindTribool                    // "Not set", "true" or "false"
	kindInt                        // a number
	kindIntText                    // a number the profile keeps as text
	kindIntChoice                  // a number picked from a list, stored by the form as text
	kindString                     // text or a choice, empty means not set
	kindSegment                    // one segment type's action in mediaSegmentActions ("intro:skip,...")
	kindSlot                       // one slot of a list such as bottomNavbarTabs
	kindShown                      // the options ticked, stored as the ones not ticked
)

type formField struct {
	Entry    string // the card in Silo
	Field    string // the field's key in Silo's form
	Key      string // the configuration key the value lands in
	Defaults bool   // a DefaultUserSettings key
	Kind     fieldKind
	Part     string   // which part of a combined value (kindSegment, kindSlot)
	Options  []string // every option (kindShown)
}

// entryOrder lists the form's entries (its cards in Silo) in page order.
var entryOrder = func() []string {
	var out []string
	for _, f := range formFields {
		if len(out) == 0 || out[len(out)-1] != f.Entry {
			out = append(out, f.Entry)
		}
	}
	return out
}()

// Configure receives the native form's saved values. Silo restarts the
// plugin after an admin saves, so this runs once per process.
func (s *Server) Configure(_ context.Context, entries []*pluginv1.ConfigEntry) error {
	native := map[string]M{}
	for _, e := range entries {
		native[e.GetKey()] = e.GetValue().AsMap()
	}
	s.setNative(native)
	return nil
}

func (s *Server) setNative(native map[string]M) {
	s.nativeMu.Lock()
	s.native = native
	s.nativeMu.Unlock()
}

func (s *Server) nativeConfig() map[string]M {
	s.nativeMu.RLock()
	defer s.nativeMu.RUnlock()
	return s.native
}

// fromNative turns the form's values into PluginConfiguration fields and
// DefaultUserSettings keys. Unset values are left out.
func fromNative(native map[string]M) (top, defaults M) {
	top, defaults = M{}, M{}
	segments, slots := map[string][]string{}, map[string][]any{}
	for _, f := range formFields {
		raw := native[f.Entry][f.Field]
		switch f.Kind {
		case kindSegment:
			if action, _ := raw.(string); action != "" {
				segments[f.Key] = append(segments[f.Key], f.Part+":"+action)
			}
			continue
		case kindSlot:
			if v, _ := raw.(string); v != "" && !slices.Contains(slots[f.Key], any(v)) {
				slots[f.Key] = append(slots[f.Key], v)
			}
			continue
		case kindShown:
			if shown, ok := raw.([]any); ok {
				var hidden []any
				for _, o := range f.Options {
					if !slices.Contains(shown, any(o)) {
						hidden = append(hidden, o)
					}
				}
				if len(hidden) > 0 {
					defaults[f.Key] = hidden
				}
			}
			continue
		}
		v, ok := convertIn(f.Kind, raw)
		if !ok {
			continue
		}
		if f.Defaults {
			defaults[f.Key] = v
		} else {
			top[f.Key] = v
		}
	}
	for k, parts := range segments {
		defaults[k] = strings.Join(parts, ",")
	}
	for k, list := range slots {
		defaults[k] = list
	}
	return top, defaults
}

func convertIn(kind fieldKind, v any) (any, bool) {
	switch kind {
	case kindBool:
		b, ok := v.(bool)
		return b, ok
	case kindTribool:
		switch v {
		case "true", true:
			return true, true
		case "false", false:
			return false, true
		}
	case kindInt, kindIntText, kindIntChoice:
		n, ok := num(v)
		if !ok {
			return nil, false
		}
		if kind == kindIntText {
			return strconv.Itoa(int(math.Round(n))), true
		}
		return int(math.Round(n)), true
	case kindString:
		if s, ok := v.(string); ok && strings.TrimSpace(s) != "" {
			return s, true
		}
	}
	return nil, false
}

// toNative is fromNative backwards, for writing the page's save into the
// form's entries. Text, choice and list fields the page cleared are written
// empty, so clearing one on the page clears it in the form.
func toNative(config M) map[string]M {
	defaults, _ := config["DefaultUserSettings"].(M)
	native := map[string]M{}
	for _, f := range formFields {
		src := config
		if f.Defaults {
			src = defaults
		}
		if native[f.Entry] == nil {
			native[f.Entry] = M{}
		}
		if v, ok := convertOut(f, src[f.Key]); ok {
			native[f.Entry][f.Field] = v
		}
	}
	return native
}

func convertOut(f formField, v any) (any, bool) {
	switch f.Kind {
	case kindSegment:
		raw, _ := v.(string)
		for _, part := range strings.Split(raw, ",") {
			if name, action, ok := strings.Cut(part, ":"); ok && strings.EqualFold(strings.TrimSpace(name), f.Part) {
				return strings.TrimSpace(action), true
			}
		}
		return "", true
	case kindSlot:
		list, _ := v.([]any)
		if i, err := strconv.Atoi(f.Part); err == nil && i >= 1 && i <= len(list) {
			if s, ok := list[i-1].(string); ok {
				return s, true
			}
		}
		return "", true
	case kindShown:
		hidden, _ := v.([]any)
		shown := []any{}
		for _, o := range f.Options {
			if !slices.Contains(hidden, any(o)) {
				shown = append(shown, o)
			}
		}
		return shown, true
	case kindBool:
		b, ok := v.(bool)
		return b, ok
	case kindTribool:
		if b, ok := v.(bool); ok {
			return strconv.FormatBool(b), true
		}
		return "", true
	case kindInt, kindIntText:
		if n, ok := convertIn(kindInt, v); ok {
			return float64(n.(int)), true
		}
		return nil, false
	case kindIntChoice:
		if n, ok := convertIn(kindInt, v); ok {
			return strconv.Itoa(n.(int)), true
		}
		return "", true
	case kindString:
		if s, ok := v.(string); ok {
			return s, true
		}
		return "", true
	}
	return nil, false
}

// The keys the form owns, so values stored with the page's other settings
// never shadow them.
var formTopKeys, formDefaultKeys = formKeys()

func formKeys() (top, defaults map[string]bool) {
	top, defaults = map[string]bool{}, map[string]bool{}
	for _, f := range formFields {
		if f.Defaults {
			defaults[f.Key] = true
		} else {
			top[f.Key] = true
		}
	}
	return top, defaults
}

// stripFormKeys removes the keys the form owns from settings the page saved.
func stripFormKeys(c M) {
	for k := range formTopKeys {
		delete(c, k)
	}
	if d, ok := c["DefaultUserSettings"].(M); ok {
		for k := range formDefaultKeys {
			delete(d, k)
		}
	}
}

// hostSetConfigEntry stores one of the form's entries in Silo.
func hostSetConfigEntry(ctx context.Context, key string, value map[string]any) error {
	host := runtime.Host()
	if host == nil {
		return store.ErrHostUnavailable
	}
	return host.SetGlobalConfigEntry(ctx, key, value)
}
