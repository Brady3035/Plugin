package api

import (
	"context"
	"strconv"

	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtime"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

type Library struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	MediaType string `json:"mediaType"`
}

// Catalog is the slice of Silo's catalog Moonbase reads through RuntimeHost.
type Catalog interface {
	// Libraries asks Silo for the libraries the account may see.
	Libraries(ctx context.Context, userID int) ([]Library, error)
}

type hostCatalog struct{}

func NewHostCatalog() Catalog { return hostCatalog{} }

func (hostCatalog) Libraries(ctx context.Context, userID int) ([]Library, error) {
	host := runtime.Host()
	if host == nil {
		return nil, store.ErrHostUnavailable
	}
	libs, err := host.ListLibraries(ctx, strconv.Itoa(userID))
	if err != nil {
		return nil, err
	}
	out := make([]Library, 0, len(libs))
	for _, l := range libs {
		out = append(out, Library{ID: l.GetId(), Name: l.GetName(), MediaType: l.GetMediaType()})
	}
	return out, nil
}
