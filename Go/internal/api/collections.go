package api

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"unicode"
)

// Each user's own order of a collection's items, so a drag-and-drop order
// follows them across devices. Item ids stay opaque strings.

const maxOrderItems = 5000

func (s *Server) collectionRoutes() {
	s.mux.HandleFunc("GET /Moonfin/Collections/{collectionId}/Order", s.user(s.handleGetCollectionOrder))
	s.mux.HandleFunc("POST /Moonfin/Collections/{collectionId}/Order", s.user(s.handleSaveCollectionOrder))
}

// orderKey keeps only the ASCII letters and digits of the collection id, which
// covers Plex's numeric ids and Silo's ids and keeps the key short.
func orderKey(id Identity, collectionID string) (string, bool) {
	safe := strings.Map(func(r rune) rune {
		if r < unicode.MaxASCII+1 && (unicode.IsLetter(r) || unicode.IsDigit(r)) {
			return r
		}
		return -1
	}, collectionID)
	if safe == "" {
		return "", false
	}
	if len(safe) > 128 {
		safe = safe[:128]
	}
	return "o/" + settingsKey(id) + "/" + safe, true
}

func (s *Server) handleGetCollectionOrder(w http.ResponseWriter, r *http.Request) {
	order := []string{}
	if key, ok := orderKey(callerOf(r), r.PathValue("collectionId")); ok {
		if _, err := s.loadJSON(r.Context(), key, &order); err != nil {
			storageFailed(w, r, err)
			return
		}
	}
	writeJSON(w, http.StatusOK, order)
}

// handleSaveCollectionOrder stores the order. An empty list clears it.
func (s *Server) handleSaveCollectionOrder(w http.ResponseWriter, r *http.Request) {
	key, ok := orderKey(callerOf(r), r.PathValue("collectionId"))
	if !ok {
		writeError(w, http.StatusBadRequest, "A collection id is required")
		return
	}
	var ids []string
	if err := json.NewDecoder(r.Body).Decode(&ids); err != nil && !errors.Is(err, io.EOF) {
		writeError(w, http.StatusBadRequest, "A JSON array of item ids is required")
		return
	}
	order := normalizeOrder(ids)
	ctx := r.Context()
	var value any = order
	if len(order) == 0 {
		// Clearing an order that was never saved shouldn't use up a key.
		found, err := s.loadJSON(ctx, key, &[]string{})
		if err != nil {
			storageFailed(w, r, err)
			return
		}
		if !found {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		value = nil
	}
	if err := s.saveJSON(ctx, key, value); err != nil {
		storageFailed(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// normalizeOrder drops blanks and repeats, keeping the first of each so the
// dragged order survives, and caps the count.
func normalizeOrder(ids []string) []string {
	out := []string{}
	seen := map[string]bool{}
	for _, id := range ids {
		id = strings.TrimSpace(id)
		if id == "" || seen[id] {
			continue
		}
		seen[id] = true
		out = append(out, id)
		if len(out) == maxOrderItems {
			break
		}
	}
	return out
}
