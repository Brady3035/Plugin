// Package store keeps Moonbase's own data in the host's per-installation
// instance state, which Silo encrypts and stores in its database.
package store

import (
	"context"
	"errors"
	"fmt"
	"sync"

	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtime"
	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtimehost"
)

// Limits the host enforces on instance state.
const (
	MaxKeys       = runtimehost.MaxInstanceStateKeys
	MaxValueBytes = runtimehost.MaxInstanceStateValueBytes
)

var ErrHostUnavailable = errors.New("silo host isn't connected yet")

// KV is the storage Moonbase needs: whole values under short keys.
type KV interface {
	Get(ctx context.Context, key string) ([]byte, bool, error)
	Put(ctx context.Context, key string, value []byte) error
}

type HostKV struct{}

func (HostKV) Get(ctx context.Context, key string) ([]byte, bool, error) {
	host := runtime.Host()
	if host == nil {
		return nil, false, ErrHostUnavailable
	}
	return host.ReadInstanceState(ctx, key)
}

func (HostKV) Put(ctx context.Context, key string, value []byte) error {
	host := runtime.Host()
	if host == nil {
		return ErrHostUnavailable
	}
	return host.WriteInstanceState(ctx, key, value)
}

// MemKV is an in-memory KV with the host's limits, for tests.
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
