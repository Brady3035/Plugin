// Package store keeps Moonbase's own data. Each host supplies a KV: Silo's
// per-installation instance state, or files on disk for the Plex companion.
package store

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"net/url"
	"os"
	"path/filepath"
	"sync"
)

// Silo's instance state limits. MemKV enforces them so tests catch code that
// would break on Silo, and the silo package checks them against the SDK.
const (
	MaxKeys       = 256
	MaxValueBytes = 256 << 10
)

// KV is the storage Moonbase needs: whole values under short keys.
type KV interface {
	Get(ctx context.Context, key string) ([]byte, bool, error)
	Put(ctx context.Context, key string, value []byte) error
}

// MemKV is an in-memory KV with Silo's limits, for tests.
type MemKV struct {
	mu   sync.Mutex
	data map[string][]byte
}

func NewMemKV() *MemKV { return &MemKV{data: map[string][]byte{}} }

func (m *MemKV) Get(_ context.Context, key string) ([]byte, bool, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	v, ok := m.data[key]
	return append([]byte(nil), v...), ok, nil
}

func (m *MemKV) Put(_ context.Context, key string, value []byte) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if len(value) > MaxValueBytes {
		return fmt.Errorf("value for %q is %d bytes, limit %d", key, len(value), MaxValueBytes)
	}
	if _, ok := m.data[key]; !ok && len(m.data) >= MaxKeys {
		return fmt.Errorf("instance state is full (%d keys)", MaxKeys)
	}
	m.data[key] = append([]byte(nil), value...)
	return nil
}

// FileKV keeps one file per key in a directory.
type FileKV struct{ Dir string }

func (f FileKV) path(key string) string {
	return filepath.Join(f.Dir, url.PathEscape(key)+".json")
}

func (f FileKV) Get(_ context.Context, key string) ([]byte, bool, error) {
	b, err := os.ReadFile(f.path(key))
	if errors.Is(err, fs.ErrNotExist) {
		return nil, false, nil
	}
	return b, err == nil, err
}

// Put writes through a temporary file so a crash never leaves half a value.
func (f FileKV) Put(_ context.Context, key string, value []byte) error {
	tmp, err := os.CreateTemp(f.Dir, ".put-*")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	if _, err := tmp.Write(value); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	return os.Rename(tmp.Name(), f.path(key))
}
