package api

import (
	"encoding/json"
	"net/http"
	"regexp"
	"time"
)

// Silo's Watch Party rooms keep no subtitle choice, so the host's pick for
// the whole room is kept here by room id. Plugins can't ask Silo who hosts
// a room: the first profile to set a room's choice owns it. Moonfin only
// lets the host set it, and room ids are UUIDs only members are told.
const keyWatchParty = "watch-party"

const (
	watchPartyTTL      = 24 * time.Hour
	watchPartyMaxRooms = 200
)

type watchPartyRoom struct {
	OwnerUserID    int       `json:"ownerUserId"`
	OwnerProfileID string    `json:"ownerProfileId"`
	Subtitle       M         `json:"subtitle,omitempty"`
	UpdatedAt      time.Time `json:"updatedAt"`
}

var roomIDPattern = regexp.MustCompile(`^[0-9a-fA-F-]{36}$`)

func (s *Server) watchPartyRoutes() {
	s.mux.HandleFunc("GET /Moonfin/WatchParty/{roomId}", s.user(s.handleGetWatchParty))
	s.mux.HandleFunc("PUT /Moonfin/WatchParty/{roomId}", s.user(s.handlePutWatchParty))
}

func (s *Server) handleGetWatchParty(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("roomId")
	if !roomIDPattern.MatchString(id) {
		writeError(w, http.StatusBadRequest, "A room id is required")
		return
	}
	var rooms map[string]watchPartyRoom
	if _, err := s.loadJSON(r.Context(), keyWatchParty, &rooms); err != nil {
		storageFailed(w, r, err)
		return
	}
	room, ok := rooms[id]
	if !ok || time.Since(room.UpdatedAt) > watchPartyTTL {
		writeJSON(w, http.StatusOK, M{})
		return
	}
	writeJSON(w, http.StatusOK, M{"subtitle": room.Subtitle})
}

// handlePutWatchParty sets the room's subtitle choice: {"subtitle": {...}}
// for a track or {"off": true}, or {"subtitle": null} to leave subtitles to
// each member.
func (s *Server) handlePutWatchParty(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("roomId")
	if !roomIDPattern.MatchString(id) {
		writeError(w, http.StatusBadRequest, "A room id is required")
		return
	}
	var body struct {
		Subtitle M `json:"subtitle"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&body); err != nil {
		writeError(w, http.StatusBadRequest, "A JSON object with subtitle is required")
		return
	}
	caller := callerOf(r)
	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	var rooms map[string]watchPartyRoom
	if _, err := s.loadJSON(ctx, keyWatchParty, &rooms); err != nil {
		storageFailed(w, r, err)
		return
	}
	if rooms == nil {
		rooms = map[string]watchPartyRoom{}
	}
	now := time.Now().UTC()
	for key, room := range rooms {
		if now.Sub(room.UpdatedAt) > watchPartyTTL {
			delete(rooms, key)
		}
	}
	room, exists := rooms[id]
	if exists && (room.OwnerUserID != caller.UserID || room.OwnerProfileID != caller.ProfileID) {
		writeError(w, http.StatusForbidden, "Only the room's host sets its subtitles")
		return
	}
	if !exists && len(rooms) >= watchPartyMaxRooms {
		writeError(w, http.StatusServiceUnavailable, "Too many Watch Party rooms are open")
		return
	}
	rooms[id] = watchPartyRoom{
		OwnerUserID:    caller.UserID,
		OwnerProfileID: caller.ProfileID,
		Subtitle:       body.Subtitle,
		UpdatedAt:      now,
	}
	if err := s.saveJSON(ctx, keyWatchParty, rooms); err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"subtitle": body.Subtitle})
}
