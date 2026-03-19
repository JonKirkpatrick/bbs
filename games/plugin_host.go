package games

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/JonKirkpatrick/bbs/games/pluginapi"
)

const (
	gamePluginsEnabledEnv   = "BBS_ENABLE_GAME_PLUGINS"
	gamePluginsDirectoryEnv = "BBS_GAME_PLUGIN_DIR"
	defaultGamePluginsDir   = "plugins/games"
	pluginRefreshInterval   = 2 * time.Second
)

const (
	pluginManifestStatusLoaded  = "loaded"
	pluginManifestStatusSkipped = "skipped"
)

// PluginManifestStatus describes discovery status for one plugin manifest file.
type PluginManifestStatus struct {
	ManifestPath    string `json:"manifest_path"`
	Name            string `json:"name,omitempty"`
	DisplayName     string `json:"display_name,omitempty"`
	Executable      string `json:"executable,omitempty"`
	ProtocolVersion int    `json:"protocol_version,omitempty"`
	Status          string `json:"status"`
	Reason          string `json:"reason,omitempty"`
}

// PluginDiagnostics summarizes plugin discovery state for runtime and dashboard views.
type PluginDiagnostics struct {
	Enabled      bool                   `json:"enabled"`
	Directory    string                 `json:"directory"`
	RefreshedAt  string                 `json:"refreshed_at,omitempty"`
	LoadedCount  int                    `json:"loaded_count"`
	SkippedCount int                    `json:"skipped_count"`
	Manifests    []PluginManifestStatus `json:"manifests,omitempty"`
}

type pluginRegistryCacheState struct {
	mu          sync.Mutex
	refreshedAt time.Time
	directory   string
	entries     map[string]gameRegistration
	viewerFiles map[string]string
	diagnostics PluginDiagnostics
}

var pluginRegistryCache pluginRegistryCacheState

func allRegistrations() map[string]gameRegistration {
	return dynamicPluginRegistrations()
}

func pluginsEnabled() bool {
	raw := strings.TrimSpace(os.Getenv(gamePluginsEnabledEnv))
	if raw == "" {
		return false
	}
	enabled, err := strconv.ParseBool(raw)
	if err != nil {
		return false
	}
	return enabled
}

func pluginsDirectory() string {
	dir := strings.TrimSpace(os.Getenv(gamePluginsDirectoryEnv))
	if dir == "" {
		dir = defaultGamePluginsDir
	}
	return filepath.Clean(dir)
}

func dynamicPluginRegistrations() map[string]gameRegistration {
	if !pluginsEnabled() {
		pluginRegistryCache.mu.Lock()
		pluginRegistryCache.directory = pluginsDirectory()
		pluginRegistryCache.entries = nil
		pluginRegistryCache.viewerFiles = nil
		pluginRegistryCache.refreshedAt = time.Time{}
		pluginRegistryCache.diagnostics = PluginDiagnostics{
			Enabled:   false,
			Directory: pluginsDirectory(),
		}
		pluginRegistryCache.mu.Unlock()
		return nil
	}

	directory := pluginsDirectory()
	now := time.Now()

	pluginRegistryCache.mu.Lock()
	defer pluginRegistryCache.mu.Unlock()

	if pluginRegistryCache.directory == directory &&
		!pluginRegistryCache.refreshedAt.IsZero() &&
		now.Sub(pluginRegistryCache.refreshedAt) < pluginRefreshInterval {
		return cloneGameRegistrations(pluginRegistryCache.entries)
	}

	scanResult := scanPluginDirectory(directory)
	pluginRegistryCache.entries = scanResult.entries
	pluginRegistryCache.viewerFiles = scanResult.viewerFiles
	pluginRegistryCache.directory = directory
	pluginRegistryCache.refreshedAt = now
	pluginRegistryCache.diagnostics = PluginDiagnostics{
		Enabled:      true,
		Directory:    directory,
		RefreshedAt:  now.UTC().Format(time.RFC3339Nano),
		LoadedCount:  scanResult.loadedCount,
		SkippedCount: scanResult.skippedCount,
		Manifests:    clonePluginManifestStatuses(scanResult.manifests),
	}
	return cloneGameRegistrations(scanResult.entries)
}

func cloneGameRegistrations(source map[string]gameRegistration) map[string]gameRegistration {
	if len(source) == 0 {
		return nil
	}
	copyMap := make(map[string]gameRegistration, len(source))
	for name, entry := range source {
		copyMap[name] = entry
	}
	return copyMap
}

func clonePluginManifestStatuses(source []PluginManifestStatus) []PluginManifestStatus {
	if len(source) == 0 {
		return nil
	}
	copySlice := make([]PluginManifestStatus, len(source))
	copy(copySlice, source)
	return copySlice
}

func clonePluginDiagnostics(source PluginDiagnostics) PluginDiagnostics {
	copyValue := source
	copyValue.Manifests = clonePluginManifestStatuses(source.Manifests)
	return copyValue
}

// CurrentPluginDiagnostics returns plugin discovery status suitable for UI diagnostics.
func CurrentPluginDiagnostics() PluginDiagnostics {
	if !pluginsEnabled() {
		return PluginDiagnostics{
			Enabled:   false,
			Directory: pluginsDirectory(),
		}
	}

	_ = dynamicPluginRegistrations()

	pluginRegistryCache.mu.Lock()
	defer pluginRegistryCache.mu.Unlock()

	if pluginRegistryCache.diagnostics.Directory == "" {
		return PluginDiagnostics{
			Enabled:   true,
			Directory: pluginsDirectory(),
		}
	}

	return clonePluginDiagnostics(pluginRegistryCache.diagnostics)
}

type pluginDirectoryScan struct {
	entries      map[string]gameRegistration
	viewerFiles  map[string]string
	manifests    []PluginManifestStatus
	loadedCount  int
	skippedCount int
}

func scanPluginDirectory(directory string) pluginDirectoryScan {
	files, err := filepath.Glob(filepath.Join(directory, "*.json"))
	if err != nil {
		return pluginDirectoryScan{
			manifests: []PluginManifestStatus{
				{
					ManifestPath: filepath.Join(directory, "*.json"),
					Status:       pluginManifestStatusSkipped,
					Reason:       fmt.Sprintf("invalid manifest glob: %v", err),
				},
			},
			skippedCount: 1,
		}
	}

	if len(files) == 0 {
		return pluginDirectoryScan{}
	}

	sort.Strings(files)
	entries := make(map[string]gameRegistration)
	viewerFiles := make(map[string]string)
	statuses := make([]PluginManifestStatus, 0, len(files))
	loadedCount := 0
	skippedCount := 0

	for _, manifestPath := range files {
		registration, name, viewerFile, status := registrationFromManifest(directory, manifestPath)

		if status.Status == pluginManifestStatusLoaded {
			if _, exists := entries[name]; exists {
				status.Status = pluginManifestStatusSkipped
				status.Reason = fmt.Sprintf("duplicate plugin name %q", name)
			}
		}

		if status.Status == pluginManifestStatusLoaded {
			entries[name] = registration
			if strings.TrimSpace(viewerFile) != "" {
				viewerFiles[name] = viewerFile
			}
			loadedCount++
		} else {
			skippedCount++
		}

		statuses = append(statuses, status)
	}

	if len(entries) == 0 {
		entries = nil
	}
	if len(viewerFiles) == 0 {
		viewerFiles = nil
	}

	return pluginDirectoryScan{
		entries:      entries,
		viewerFiles:  viewerFiles,
		manifests:    statuses,
		loadedCount:  loadedCount,
		skippedCount: skippedCount,
	}
}

func registrationFromManifest(directory, manifestPath string) (gameRegistration, string, string, PluginManifestStatus) {
	status := PluginManifestStatus{
		ManifestPath: manifestPath,
		Status:       pluginManifestStatusSkipped,
	}

	raw, err := os.ReadFile(manifestPath)
	if err != nil {
		status.Reason = fmt.Sprintf("failed reading manifest: %v", err)
		fmt.Printf("[game-plugin] %s: %s\n", manifestPath, status.Reason)
		return gameRegistration{}, "", "", status
	}

	var manifest pluginapi.Manifest
	if err := json.Unmarshal(raw, &manifest); err != nil {
		status.Reason = fmt.Sprintf("failed decoding manifest: %v", err)
		fmt.Printf("[game-plugin] %s: %s\n", manifestPath, status.Reason)
		return gameRegistration{}, "", "", status
	}

	status.Name = strings.ToLower(strings.TrimSpace(manifest.Name))
	status.DisplayName = strings.TrimSpace(manifest.DisplayName)
	status.Executable = strings.TrimSpace(manifest.Executable)
	status.ProtocolVersion = manifest.ProtocolVersion

	if manifest.ProtocolVersion == 0 {
		manifest.ProtocolVersion = pluginapi.ProtocolVersion
		status.ProtocolVersion = manifest.ProtocolVersion
	}
	if manifest.ProtocolVersion != pluginapi.ProtocolVersion {
		status.Reason = fmt.Sprintf("protocol_version=%d (expected %d)", manifest.ProtocolVersion, pluginapi.ProtocolVersion)
		fmt.Printf("[game-plugin] skipping %s: %s\n", manifestPath, status.Reason)
		return gameRegistration{}, "", "", status
	}

	name := strings.ToLower(strings.TrimSpace(manifest.Name))
	if name == "" {
		status.Reason = "missing name"
		fmt.Printf("[game-plugin] skipping %s: %s\n", manifestPath, status.Reason)
		return gameRegistration{}, "", "", status
	}

	execPath, err := resolvePluginExecutable(directory, manifestPath, manifest.Executable)
	if err != nil {
		status.Reason = err.Error()
		fmt.Printf("[game-plugin] skipping %s: %s\n", manifestPath, status.Reason)
		return gameRegistration{}, "", "", status
	}
	status.Executable = execPath

	viewerClientEntry := strings.TrimSpace(manifest.ViewerClientEntry)
	if viewerClientEntry == "" {
		status.Reason = "missing viewer_client_entry"
		fmt.Printf("[game-plugin] skipping %s: %s\n", manifestPath, status.Reason)
		return gameRegistration{}, "", "", status
	}
	if strings.Contains(viewerClientEntry, "..") {
		status.Reason = "viewer_client_entry must not contain '..'"
		fmt.Printf("[game-plugin] skipping %s: %s\n", manifestPath, status.Reason)
		return gameRegistration{}, "", "", status
	}

	viewerClientFile, err := resolvePluginViewerClientEntry(directory, manifestPath, viewerClientEntry)
	if err != nil {
		status.Reason = err.Error()
		fmt.Printf("[game-plugin] skipping %s: %s\n", manifestPath, status.Reason)
		return gameRegistration{}, "", "", status
	}

	args := make([]GameArgSpec, 0, len(manifest.Args))
	for _, arg := range manifest.Args {
		args = append(args, GameArgSpec{
			Key:          arg.Key,
			Label:        arg.Label,
			InputType:    arg.InputType,
			Placeholder:  arg.Placeholder,
			DefaultValue: arg.DefaultValue,
			Required:     arg.Required,
			Help:         arg.Help,
		})
	}

	displayName := strings.TrimSpace(manifest.DisplayName)
	if displayName == "" {
		displayName = name
	}

	capturedName := name
	capturedExecutable := execPath
	registration := gameRegistration{
		Factory: func(args []string) (GameInstance, error) {
			return launchPluginGame(capturedName, capturedExecutable, args)
		},
		Catalog: GameCatalogEntry{
			Name:              name,
			DisplayName:       displayName,
			Args:              args,
			ViewerClientEntry: viewerClientEntry,
			SupportsReplay:    manifest.SupportsReplay,
			SupportsMoveClock: manifest.SupportsMoveClock,
			SupportsHandicap:  manifest.SupportsHandicap,
		},
	}

	status.Status = pluginManifestStatusLoaded
	status.Reason = ""
	status.Name = capturedName
	if status.DisplayName == "" {
		status.DisplayName = displayName
	}

	return registration, capturedName, viewerClientFile, status
}

func resolvePluginExecutable(directory, manifestPath, executable string) (string, error) {
	executable = strings.TrimSpace(executable)
	if executable == "" {
		return "", fmt.Errorf("manifest %s is missing executable", manifestPath)
	}

	tryPaths := make([]string, 0, 3)
	if filepath.IsAbs(executable) {
		tryPaths = append(tryPaths, executable)
	} else {
		tryPaths = append(tryPaths,
			filepath.Join(filepath.Dir(manifestPath), executable),
			filepath.Join(directory, executable),
			executable,
		)
	}

	for _, candidate := range tryPaths {
		if info, err := os.Stat(candidate); err == nil && !info.IsDir() {
			return filepath.Clean(candidate), nil
		}
	}

	return "", fmt.Errorf("executable %q was not found", executable)
}

func resolvePluginViewerClientEntry(directory, manifestPath, entry string) (string, error) {
	entry = strings.TrimSpace(entry)
	if entry == "" {
		return "", nil
	}

	tryPaths := make([]string, 0, 2)
	if filepath.IsAbs(entry) {
		tryPaths = append(tryPaths, entry)
	} else {
		tryPaths = append(tryPaths,
			filepath.Join(filepath.Dir(manifestPath), entry),
			filepath.Join(directory, entry),
		)
	}

	for _, candidate := range tryPaths {
		if info, err := os.Stat(candidate); err == nil && !info.IsDir() {
			return filepath.Clean(candidate), nil
		}
	}

	return "", fmt.Errorf("viewer_client_entry %q was not found", entry)
}

// PluginViewerClientFile returns the absolute viewer client entry file for a game plugin.
func PluginViewerClientFile(gameName string) (string, bool) {
	lookup := strings.ToLower(strings.TrimSpace(gameName))
	if lookup == "" {
		return "", false
	}

	_ = dynamicPluginRegistrations()

	pluginRegistryCache.mu.Lock()
	defer pluginRegistryCache.mu.Unlock()

	if len(pluginRegistryCache.viewerFiles) == 0 {
		return "", false
	}

	path, ok := pluginRegistryCache.viewerFiles[lookup]
	if !ok || strings.TrimSpace(path) == "" {
		return "", false
	}

	return path, true
}
