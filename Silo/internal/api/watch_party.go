package api

import (
	"encoding/json"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"
)

// Silo's Watch Party rooms keep no subtitle choice and share no member
// avatars, so both are kept here by room id: the host's subtitle pick for
// the whole room, and each member's own avatar link (Silo's preset links
// are public, an upload's is signed for 15 minutes, so members post theirs
// again before it runs out). Plugins can't ask Silo who hosts a room: the
// first profile to set a room's subtitles owns them. Moonfin only lets the
// host set them, and room ids are UUIDs only members are told.
const keyWatchParty = "watch-party"

const (
	watchPartyTTL      = 24 * time.Hour
	watchPartyMaxRooms = 200
)

type watchPartyRoom struct {
	OwnerUserID    int    `json:"ownerUserId"`
	OwnerProfileID string `json:"ownerProfileId"`
	Subtitle       M      `json:"subtitle,omitempty"`
	// Avatars are members' avatar links by Silo profile id.
	Avatars   map[string]string `json:"avatars,omitempty"`
	UpdatedAt time.Time         `json:"updatedAt"`
}

const maxRoomAvatars = 50

var roomIDPattern = regexp.MustCompile(`^[0-9a-fA-F-]{36}$`)

func (s *Server) watchPartyRoutes() {
	s.mux.HandleFunc("GET /Moonfin/WatchParty/{roomId}", s.user(s.handleGetWatchParty))
	s.mux.HandleFunc("PUT /Moonfin/WatchParty/{roomId}", s.user(s.handlePutWatchParty))
	s.mux.HandleFunc("PUT /Moonfin/WatchParty/{roomId}/Avatar", s.user(s.handlePutWatchPartyAvatar))
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
	writeJSON(w, http.StatusOK, M{"subtitle": room.Subtitle, "avatars": room.Avatars})
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
	s.updateWatchParty(w, r, id, func(room *watchPartyRoom) (int, string) {
		if room.OwnerProfileID != "" &&
			(room.OwnerUserID != caller.UserID || room.OwnerProfileID != caller.ProfileID) {
			return http.StatusForbidden, "Only the room's host sets its subtitles"
		}
		room.OwnerUserID, room.OwnerProfileID = caller.UserID, caller.ProfileID
		room.Subtitle = body.Subtitle
		return 0, ""
	}, M{"subtitle": body.Subtitle})
}

// handlePutWatchPartyAvatar sets the caller's own avatar link for the room:
// {"url": "..."}, an absolute http(s) link or a path on this server.
func (s *Server) handlePutWatchPartyAvatar(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("roomId")
	if !roomIDPattern.MatchString(id) {
		writeError(w, http.StatusBadRequest, "A room id is required")
		return
	}
	var body struct {
		URL string `json:"url"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&body); err != nil ||
		!validAvatarLink(body.URL) {
		writeError(w, http.StatusBadRequest, "An http(s) or server-relative avatar url is required")
		return
	}
	caller := callerOf(r)
	s.updateWatchParty(w, r, id, func(room *watchPartyRoom) (int, string) {
		if room.Avatars == nil {
			room.Avatars = map[string]string{}
		}
		if _, ok := room.Avatars[caller.ProfileID]; !ok && len(room.Avatars) >= maxRoomAvatars {
			return http.StatusServiceUnavailable, "Too many members in this room"
		}
		room.Avatars[caller.ProfileID] = body.URL
		return 0, ""
	}, M{"url": body.URL})
}

func validAvatarLink(link string) bool {
	if len(link) == 0 || len(link) > 2048 {
		return false
	}
	u, err := url.Parse(link)
	if err != nil {
		return false
	}
	if u.Scheme == "" {
		return u.Host == "" && strings.HasPrefix(u.Path, "/")
	}
	return (u.Scheme == "https" || u.Scheme == "http") && u.Host != ""
}

// updateWatchParty applies change to room id under the lock, after expiring
// old rooms, and answers with reply. change returns a status and message to
// refuse with, or 0.
func (s *Server) updateWatchParty(
	w http.ResponseWriter,
	r *http.Request,
	id string,
	change func(room *watchPartyRoom) (int, string),
	reply M,
) {
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
	if !exists && len(rooms) >= watchPartyMaxRooms {
		writeError(w, http.StatusServiceUnavailable, "Too many Watch Party rooms are open")
		return
	}
	if status, message := change(&room); status != 0 {
		writeError(w, status, message)
		return
	}
	room.UpdatedAt = now
	rooms[id] = room
	if err := s.saveJSON(ctx, keyWatchParty, rooms); err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, reply)
}
