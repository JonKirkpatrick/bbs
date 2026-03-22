package stadium

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"
)

// ServerIdentity is the long-lived server identity used for federation and audit.
type ServerIdentity struct {
	LocalServerID        string
	GlobalServerID       string
	PublicKeyFingerprint string
	PublicKeyHex         string
	PrivateKeyHex        string
	PreferredDisplayName string
	AcceptedDisplayName  string
	RegistryStatus       string
	CreatedAt            time.Time
	LastRegistrationAt   time.Time
}

// BuildNewLocalServerIdentity generates a stable local identity material for first bootstrap.
func BuildNewLocalServerIdentity(preferredDisplayName string) (ServerIdentity, error) {
	publicKey, privateKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return ServerIdentity{}, err
	}

	digest := sha256.Sum256(publicKey)
	fingerprint := hex.EncodeToString(digest[:])
	localID := "srv_" + fingerprint[:32]
	now := time.Now().UTC()

	return ServerIdentity{
		LocalServerID:        localID,
		PublicKeyFingerprint: fingerprint,
		PublicKeyHex:         hex.EncodeToString(publicKey),
		PrivateKeyHex:        hex.EncodeToString(privateKey),
		PreferredDisplayName: strings.TrimSpace(preferredDisplayName),
		RegistryStatus:       "pending",
		CreatedAt:            now,
	}, nil
}

// ServerRegistrationRequest is a federation registration request payload.
type ServerRegistrationRequest struct {
	LocalServerID        string
	PublicKeyFingerprint string
	PreferredDisplayName string
	SoftwareVersion      string
}

// ServerRegistrationResponse is returned by a registry implementation.
type ServerRegistrationResponse struct {
	GlobalServerID      string
	AcceptedDisplayName string
	Status              string
	NameConflict        bool
	Suggestions         []string
	Message             string
	IssuedAt            time.Time
}

// GlobalServerRegistrar defines how a local server obtains/refreshes global registration.
type GlobalServerRegistrar interface {
	RegisterServer(ctx context.Context, req ServerRegistrationRequest) (ServerRegistrationResponse, error)
}

// MockGlobalServerRegistrar is an in-memory registrar for local development and testing.
type MockGlobalServerRegistrar struct {
	mu             sync.Mutex
	byLocalID      map[string]ServerRegistrationResponse
	nameOwnerships map[string]string
	nextID         int
}

func NewMockGlobalServerRegistrar() *MockGlobalServerRegistrar {
	return &MockGlobalServerRegistrar{
		byLocalID:      make(map[string]ServerRegistrationResponse),
		nameOwnerships: make(map[string]string),
		nextID:         1,
	}
}

func (r *MockGlobalServerRegistrar) RegisterServer(_ context.Context, req ServerRegistrationRequest) (ServerRegistrationResponse, error) {
	r.mu.Lock()
	defer r.mu.Unlock()

	if strings.TrimSpace(req.LocalServerID) == "" {
		return ServerRegistrationResponse{}, errors.New("local_server_id is required")
	}
	if strings.TrimSpace(req.PublicKeyFingerprint) == "" {
		return ServerRegistrationResponse{}, errors.New("public_key_fingerprint is required")
	}

	displayName := normalizeDisplayName(req.PreferredDisplayName)
	if displayName == "" {
		displayName = req.LocalServerID
	}

	if existing, ok := r.byLocalID[req.LocalServerID]; ok {
		if currentOwner, exists := r.nameOwnerships[displayName]; !exists || currentOwner == req.LocalServerID {
			r.nameOwnerships[displayName] = req.LocalServerID
			existing.AcceptedDisplayName = displayName
			existing.NameConflict = false
			existing.Suggestions = nil
			existing.Status = "active"
			existing.IssuedAt = time.Now().UTC()
			r.byLocalID[req.LocalServerID] = existing
			return existing, nil
		}

		suggestions := r.suggestNames(displayName)
		existing.Status = "active"
		existing.NameConflict = true
		existing.Suggestions = suggestions
		existing.Message = "preferred display name already in use"
		existing.IssuedAt = time.Now().UTC()
		r.byLocalID[req.LocalServerID] = existing
		return existing, nil
	}

	response := ServerRegistrationResponse{
		GlobalServerID: fmt.Sprintf("gsrv_%08d", r.nextID),
		Status:         "active",
		IssuedAt:       time.Now().UTC(),
	}
	r.nextID++

	owner, inUse := r.nameOwnerships[displayName]
	if !inUse || owner == req.LocalServerID {
		response.AcceptedDisplayName = displayName
		r.nameOwnerships[displayName] = req.LocalServerID
	} else {
		response.AcceptedDisplayName = req.LocalServerID
		response.NameConflict = true
		response.Suggestions = r.suggestNames(displayName)
		response.Message = "preferred display name already in use"
	}

	r.byLocalID[req.LocalServerID] = response
	return response, nil
}

func (r *MockGlobalServerRegistrar) suggestNames(base string) []string {
	suggestions := make([]string, 0, 3)
	for i := 1; i <= 3; i++ {
		candidate := fmt.Sprintf("%s-%d", base, i)
		if _, exists := r.nameOwnerships[candidate]; exists {
			continue
		}
		suggestions = append(suggestions, candidate)
	}
	if len(suggestions) == 0 {
		suggestions = append(suggestions, base+"-alt")
	}
	sort.Strings(suggestions)
	return suggestions
}

func normalizeDisplayName(raw string) string {
	name := strings.TrimSpace(raw)
	if name == "" {
		return ""
	}
	name = strings.ToLower(name)
	name = strings.ReplaceAll(name, " ", "-")
	for strings.Contains(name, "--") {
		name = strings.ReplaceAll(name, "--", "-")
	}
	return strings.Trim(name, "-")
}
