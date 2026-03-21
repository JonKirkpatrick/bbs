package games

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/JonKirkpatrick/bbs/games/pluginapi"
)

func writeTestFile(t *testing.T, path string, data []byte) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatalf("MkdirAll(%q) failed: %v", path, err)
	}
	if err := os.WriteFile(path, data, 0o755); err != nil {
		t.Fatalf("WriteFile(%q) failed: %v", path, err)
	}
}

func writeManifest(t *testing.T, path string, manifest pluginapi.Manifest) {
	t.Helper()
	data, err := json.Marshal(manifest)
	if err != nil {
		t.Fatalf("Marshal manifest failed: %v", err)
	}
	writeTestFile(t, path, data)
}

func TestResolvePluginExecutable_PathRules(t *testing.T) {
	tmp := t.TempDir()
	manifestPath := filepath.Join(tmp, "plugins", "game.json")
	manifestDir := filepath.Dir(manifestPath)
	pluginDir := filepath.Join(tmp, "plugins")

	relExec := filepath.Join(manifestDir, "bin", "game-plugin")
	writeTestFile(t, relExec, []byte("#!/bin/sh\nexit 0\n"))

	absExec := filepath.Join(tmp, "abs-plugin")
	writeTestFile(t, absExec, []byte("#!/bin/sh\nexit 0\n"))

	got, err := resolvePluginExecutable(pluginDir, manifestPath, "bin/game-plugin")
	if err != nil {
		t.Fatalf("resolvePluginExecutable relative failed: %v", err)
	}
	if got != filepath.Clean(relExec) {
		t.Fatalf("relative executable path = %q, want %q", got, filepath.Clean(relExec))
	}

	got, err = resolvePluginExecutable(pluginDir, manifestPath, absExec)
	if err != nil {
		t.Fatalf("resolvePluginExecutable absolute failed: %v", err)
	}
	if got != filepath.Clean(absExec) {
		t.Fatalf("absolute executable path = %q, want %q", got, filepath.Clean(absExec))
	}

	_, err = resolvePluginExecutable(pluginDir, manifestPath, "missing-file")
	if err == nil {
		t.Fatal("expected missing executable error")
	}
}

func TestResolvePluginViewerClientEntry_PathRules(t *testing.T) {
	tmp := t.TempDir()
	manifestPath := filepath.Join(tmp, "plugins", "game.json")
	manifestDir := filepath.Dir(manifestPath)
	pluginDir := filepath.Join(tmp, "plugins")

	relViewer := filepath.Join(manifestDir, "viewer", "game.js")
	writeTestFile(t, relViewer, []byte("console.log('ok');\n"))

	absViewer := filepath.Join(tmp, "viewer-abs.js")
	writeTestFile(t, absViewer, []byte("console.log('abs');\n"))

	got, err := resolvePluginViewerClientEntry(pluginDir, manifestPath, "viewer/game.js")
	if err != nil {
		t.Fatalf("resolvePluginViewerClientEntry relative failed: %v", err)
	}
	if got != filepath.Clean(relViewer) {
		t.Fatalf("relative viewer path = %q, want %q", got, filepath.Clean(relViewer))
	}

	got, err = resolvePluginViewerClientEntry(pluginDir, manifestPath, absViewer)
	if err != nil {
		t.Fatalf("resolvePluginViewerClientEntry absolute failed: %v", err)
	}
	if got != filepath.Clean(absViewer) {
		t.Fatalf("absolute viewer path = %q, want %q", got, filepath.Clean(absViewer))
	}

	_, err = resolvePluginViewerClientEntry(pluginDir, manifestPath, "missing.js")
	if err == nil {
		t.Fatal("expected missing viewer_client_entry error")
	}
}

func TestRegistrationFromManifest_ValidationFailures(t *testing.T) {
	tmp := t.TempDir()
	manifestPath := filepath.Join(tmp, "bad.json")

	writeTestFile(t, manifestPath, []byte(`{"name":"x"`))
	_, _, _, status := registrationFromManifest(tmp, manifestPath)
	if status.Status != pluginManifestStatusSkipped || !strings.Contains(status.Reason, "failed decoding manifest") {
		t.Fatalf("unexpected decode failure status: %+v", status)
	}

	manifest := pluginapi.Manifest{
		ProtocolVersion:   pluginapi.ProtocolVersion,
		Name:              "x",
		DisplayName:       "X",
		Executable:        "plugin-bin",
		ViewerClientEntry: "",
	}
	writeManifest(t, manifestPath, manifest)
	writeTestFile(t, filepath.Join(tmp, "plugin-bin"), []byte("#!/bin/sh\nexit 0\n"))
	_, _, _, status = registrationFromManifest(tmp, manifestPath)
	if status.Status != pluginManifestStatusSkipped || !strings.Contains(status.Reason, "missing viewer_client_entry") {
		t.Fatalf("unexpected missing viewer status: %+v", status)
	}

	manifest.ViewerClientEntry = "../escape.js"
	writeManifest(t, manifestPath, manifest)
	_, _, _, status = registrationFromManifest(tmp, manifestPath)
	if status.Status != pluginManifestStatusSkipped || !strings.Contains(status.Reason, "must not contain '..'") {
		t.Fatalf("unexpected traversal status: %+v", status)
	}

	manifest.ViewerClientEntry = "viewer.js"
	manifest.ProtocolVersion = pluginapi.ProtocolVersion + 1
	writeManifest(t, manifestPath, manifest)
	_, _, _, status = registrationFromManifest(tmp, manifestPath)
	if status.Status != pluginManifestStatusSkipped || !strings.Contains(status.Reason, "protocol_version") {
		t.Fatalf("unexpected protocol mismatch status: %+v", status)
	}
}

func TestDynamicPluginRegistrations_DiagnosticsAndViewerLookup(t *testing.T) {
	resetPluginCacheForTests()
	tmp := t.TempDir()
	manifestPath := filepath.Join(tmp, "demo.json")
	execPath := filepath.Join(tmp, "demo-plugin")
	viewerPath := filepath.Join(tmp, "demo-viewer.js")

	writeTestFile(t, execPath, []byte("#!/bin/sh\nexit 0\n"))
	writeTestFile(t, viewerPath, []byte("console.log('demo');\n"))
	writeManifest(t, manifestPath, pluginapi.Manifest{
		ProtocolVersion:   pluginapi.ProtocolVersion,
		Name:              "demo",
		DisplayName:       "Demo",
		Executable:        filepath.Base(execPath),
		ViewerClientEntry: filepath.Base(viewerPath),
		SupportsReplay:    true,
		SupportsMoveClock: true,
		SupportsHandicap:  true,
	})

	t.Setenv(gamePluginsEnabledEnv, "true")
	t.Setenv(gamePluginsDirectoryEnv, tmp)

	regs := dynamicPluginRegistrations()
	if len(regs) != 1 {
		t.Fatalf("registrations len = %d, want 1", len(regs))
	}
	if _, ok := regs["demo"]; !ok {
		t.Fatalf("expected registration for demo")
	}

	diag := CurrentPluginDiagnostics()
	if !diag.Enabled || diag.Directory != filepath.Clean(tmp) {
		t.Fatalf("unexpected diagnostics header: %+v", diag)
	}
	if diag.LoadedCount != 1 || diag.SkippedCount != 0 {
		t.Fatalf("unexpected diagnostics counts: %+v", diag)
	}

	viewer, ok := PluginViewerClientFile("demo")
	if !ok {
		t.Fatalf("expected viewer client entry for demo")
	}
	if viewer != filepath.Clean(viewerPath) {
		t.Fatalf("viewer path = %q, want %q", viewer, filepath.Clean(viewerPath))
	}
}
