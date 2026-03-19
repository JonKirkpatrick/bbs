package games

import (
	"fmt"
	"sort"
	"strings"
)

// GameFactory receives command line arguments and returns a game instance.
// All games must be external plugins; there are no built-in games.
type GameFactory func(args []string) (GameInstance, error)

// GameArgSpec describes one optional/required argument for arena creation.
type GameArgSpec struct {
	Key          string `json:"key"`
	Label        string `json:"label"`
	InputType    string `json:"input_type"`
	Placeholder  string `json:"placeholder,omitempty"`
	DefaultValue string `json:"default_value,omitempty"`
	Required     bool   `json:"required,omitempty"`
	Help         string `json:"help,omitempty"`
}

// GameCatalogEntry describes game metadata for dashboard UIs.
// All games must declare a viewer_client_entry for client-side rendering.
type GameCatalogEntry struct {
	Name              string        `json:"name"`
	DisplayName       string        `json:"display_name"`
	Args              []GameArgSpec `json:"args,omitempty"`
	ViewerClientEntry string        `json:"viewer_client_entry"`
	SupportsReplay    bool          `json:"supports_replay"`
	SupportsMoveClock bool          `json:"supports_move_clock"`
	SupportsHandicap  bool          `json:"supports_handicap"`
}

type gameRegistration struct {
	Factory GameFactory
	Catalog GameCatalogEntry
}

func GetGame(name string, args []string) (GameInstance, error) {
	lookupName := strings.ToLower(strings.TrimSpace(name))
	registrations := allRegistrations()
	registration, exists := registrations[lookupName]
	if !exists {
		return nil, fmt.Errorf("game '%s' not found", lookupName)
	}
	return registration.Factory(args)
}

// AvailableGameCatalog returns a stable, sorted list of plugin game metadata.
func AvailableGameCatalog() []GameCatalogEntry {
	registrations := allRegistrations()
	names := make([]string, 0, len(registrations))
	for name := range registrations {
		names = append(names, name)
	}
	sort.Strings(names)

	entries := make([]GameCatalogEntry, 0, len(names))
	for _, name := range names {
		entry := registrations[name].Catalog
		if len(entry.Args) > 0 {
			argsCopy := make([]GameArgSpec, len(entry.Args))
			copy(argsCopy, entry.Args)
			entry.Args = argsCopy
		}
		entries = append(entries, entry)
	}

	return entries
}
