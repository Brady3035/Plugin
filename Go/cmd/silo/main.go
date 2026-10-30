package main

import (
	_ "embed"

	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtime"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// version is set at build time with -ldflags "-X main.version=...".
var version string

//go:embed manifest.json
var manifestJSON []byte

func main() {
	server := api.NewServer(version, store.HostKV{}, api.NewHostCatalog())
	runtime.ServeManifestWithOptions(manifestJSON, version,
		runtime.CapabilityServers{HttpRoutes: server}, runtime.WithConfigure(server.Configure))
}
