package games

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/JonKirkpatrick/bbs/games/pluginapi"
)

func resetPluginCacheForTests() {
	pluginRegistryCache.mu.Lock()
	pluginRegistryCache.refreshedAt = pluginRegistryCache.refreshedAt.Add(-pluginRefreshInterval * 2)
	pluginRegistryCache.directory = ""
	pluginRegistryCache.entries = nil
	pluginRegistryCache.viewerFiles = nil
	pluginRegistryCache.diagnostics = PluginDiagnostics{}
	pluginRegistryCache.mu.Unlock()
}

func TestGetGame_NotFound(t *testing.T) {
	resetPluginCacheForTests()
	t.Setenv(gamePluginsEnabledEnv, "false")

	_, err := GetGame("missing_game", nil)
	if err == nil {
		t.Fatal("expected not found error")
	}
}

func TestAvailableGameCatalog_WhenPluginsDisabled(t *testing.T) {
	resetPluginCacheForTests()
	t.Setenv(gamePluginsEnabledEnv, "false")

	catalog := AvailableGameCatalog()
	if len(catalog) != 0 {
		t.Fatalf("catalog length = %d, want 0", len(catalog))
	}
}

func TestAvailableGameCatalog_IsDefensiveCopy(t *testing.T) {
	resetPluginCacheForTests()
	tmp := t.TempDir()

	write := func(path string, data []byte, mode os.FileMode) {
		t.Helper()
		if err := os.WriteFile(path, data, mode); err != nil {
			t.Fatalf("WriteFile(%q) failed: %v", path, err)
		}
	}

	execA := filepath.Join(tmp, "alpha-plugin")
	viewerA := filepath.Join(tmp, "alpha-viewer.js")
	execB := filepath.Join(tmp, "beta-plugin")
	viewerB := filepath.Join(tmp, "beta-viewer.js")
	write(execA, []byte("#!/bin/sh\nexit 0\n"), 0o755)
	write(viewerA, []byte("console.log('a');\n"), 0o644)
	write(execB, []byte("#!/bin/sh\nexit 0\n"), 0o755)
	write(viewerB, []byte("console.log('b');\n"), 0o644)

	manifestA := pluginapi.Manifest{
		ProtocolVersion:   pluginapi.ProtocolVersion,
		Name:              "alpha",
		DisplayName:       "Alpha",
		Executable:        filepath.Base(execA),
		ViewerClientEntry: filepath.Base(viewerA),
		SupportsMoveClock: true,
		SupportsHandicap:  true,
		Args: []pluginapi.ArgSpec{
			{Key: "k1", Label: "K1", InputType: "text"},
		},
	}
	manifestB := pluginapi.Manifest{
		ProtocolVersion:   pluginapi.ProtocolVersion,
		Name:              "beta",
		DisplayName:       "Beta",
		Executable:        filepath.Base(execB),
		ViewerClientEntry: filepath.Base(viewerB),
		SupportsMoveClock: true,
		SupportsHandicap:  true,
	}

	rawA, err := json.Marshal(manifestA)
	if err != nil {
		t.Fatalf("Marshal manifestA failed: %v", err)
	}
	rawB, err := json.Marshal(manifestB)
	if err != nil {
		t.Fatalf("Marshal manifestB failed: %v", err)
	}
	write(filepath.Join(tmp, "alpha.json"), rawA, 0o644)
	write(filepath.Join(tmp, "beta.json"), rawB, 0o644)

	t.Setenv(gamePluginsEnabledEnv, "true")
	t.Setenv(gamePluginsDirectoryEnv, tmp)

	catalog := AvailableGameCatalog()
	if len(catalog) != 2 {
		t.Fatalf("catalog length = %d, want 2", len(catalog))
	}
	if catalog[0].Name != "alpha" || catalog[1].Name != "beta" {
		t.Fatalf("catalog not sorted by name: %+v", catalog)
	}

	catalog[0].Args[0].Key = "mutated"

	catalog2 := AvailableGameCatalog()
	if catalog2[0].Args[0].Key != "k1" {
		t.Fatalf("expected args slice to be defensive copy, got %+v", catalog2[0].Args)
	}
}
