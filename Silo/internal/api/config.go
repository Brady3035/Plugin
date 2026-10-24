package api

import (
	"context"
	"log/slog"
	"maps"
	"strings"

	"github.com/Moonfin-Client/Plugin/Silo/internal/pluginconfig"
)

// Config is what the routes need from the plugin configuration.
type Config struct {
	EnableSettingsSync bool
	MdblistAPIKey      string
	TmdbAPIKey         string
	SeerrDisplayName   string
	// Defaults is DefaultUserSettings, the admin's defaults for every profile.
	Defaults M
}

// loadPageConfig reads the settings only the Moonbase page edits, with any
// key the native form owns removed.
func (s *Server) loadPageConfig(ctx context.Context) (M, error) {
	var c M
	if _, err := s.loadJSON(ctx, keyConfig, &c); err != nil {
		return nil, err
	}
	stripFormKeys(c)
	return c, nil
}

// fullConfig is the whole PluginConfiguration as the Jellyfin plugin would
// have it.
func (s *Server) fullConfig(ctx context.Context) (M, error) {
	page, err := s.loadPageConfig(ctx)
	if err != nil {
		return nil, err
	}
	return s.mergeConfig(page), nil
}

// mergeConfig lays a fresh install's values, then the page-only settings
// in page, then the native form's values.
func (s *Server) mergeConfig(page M) M {
	top, defaults := fromNative(s.nativeConfig())
	out := pluginconfig.Defaults()
	maps.Copy(out, page)
	maps.Copy(out, top)
	merged, _ := out["DefaultUserSettings"].(M)
	if merged == nil {
		merged = M{}
	}
	maps.Copy(merged, defaults)
	if len(merged) > 0 {
		out["DefaultUserSettings"] = merged
	} else {
		delete(out, "DefaultUserSettings")
	}
	return out
}

// config reads the configuration. If storage fails it logs and carries on
// with the native form's values alone, so routes keep working.
func (s *Server) config(ctx context.Context) Config {
	c, err := s.fullConfig(ctx)
	if err != nil {
		slog.ErrorContext(ctx, "load plugin configuration", "error", err)
		c = s.mergeConfig(nil)
	}
	return configFrom(c)
}

func configFrom(c M) Config {
	cfg := Config{EnableSettingsSync: true}
	if v, ok := c["EnableSettingsSync"].(bool); ok {
		cfg.EnableSettingsSync = v
	}
	cfg.MdblistAPIKey = strings.TrimSpace(str(c["MdblistApiKey"]))
	cfg.TmdbAPIKey = strings.TrimSpace(str(c["TmdbApiKey"]))
	cfg.SeerrDisplayName = strings.TrimSpace(str(c["SeerrDisplayName"]))
	cfg.Defaults, _ = c["DefaultUserSettings"].(M)
	return cfg
}

func str(v any) string {
	s, _ := v.(string)
	return s
}
