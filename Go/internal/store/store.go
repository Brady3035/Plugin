// Package store keeps Moonbase's own data in a KV the host supplies, such as
// Silo's per-installation instance state.
package store

import (
	"context"
	"fmt"
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
