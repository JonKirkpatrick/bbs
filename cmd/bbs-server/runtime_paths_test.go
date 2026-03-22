package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestResolveRuntimePathsWith_RepoRootDetection(t *testing.T) {
	tmp := t.TempDir()
	templatesDir := filepath.Join(tmp, "cmd", "bbs-server", "templates")
	pluginsDir := filepath.Join(tmp, "cmd", "bbs-server", "plugins", "games")

	if err := os.MkdirAll(templatesDir, 0o755); err != nil {
		t.Fatalf("ensure templates dir: %v", err)
	}
	if err := os.MkdirAll(pluginsDir, 0o755); err != nil {
		t.Fatalf("ensure plugins dir: %v", err)
	}

	paths, err := resolveRuntimePathsWith(func(_ string) string { return "" }, func() (string, error) {
		return tmp, nil
	})
	if err != nil {
		t.Fatalf("resolveRuntimePathsWith returned error: %v", err)
	}

	if paths.ServerHome != filepath.Clean(tmp) {
		t.Fatalf("ServerHome = %q, want %q", paths.ServerHome, filepath.Clean(tmp))
	}
	if paths.TemplatesDir != filepath.Clean(templatesDir) {
		t.Fatalf("TemplatesDir = %q, want %q", paths.TemplatesDir, filepath.Clean(templatesDir))
	}
	if paths.PluginsDir != filepath.Clean(pluginsDir) {
		t.Fatalf("PluginsDir = %q, want %q", paths.PluginsDir, filepath.Clean(pluginsDir))
	}

	wantDataDir := filepath.Join(tmp, "data")
	if paths.DataDir != filepath.Clean(wantDataDir) {
		t.Fatalf("DataDir = %q, want %q", paths.DataDir, filepath.Clean(wantDataDir))
	}
	wantSQLite := filepath.Join(wantDataDir, "bbs.sqlite3")
	if paths.SQLitePath != filepath.Clean(wantSQLite) {
		t.Fatalf("SQLitePath = %q, want %q", paths.SQLitePath, filepath.Clean(wantSQLite))
	}
}

func TestResolveRuntimePathsWith_EnvOverrides(t *testing.T) {
	getenv := func(key string) string {
		overrides := map[string]string{
			serverHomeEnv:      "/srv/bbs",
			serverConfigDirEnv: "/etc/bbs",
			serverDataDirEnv:   "/var/lib/bbs",
			templateDirEnv:     "/opt/bbs/templates",
			pluginDirEnv:       "/opt/bbs/plugins/games",
			"BBS_SQLITE_PATH":  "/var/lib/bbs/state.db",
		}
		return overrides[key]
	}

	paths, err := resolveRuntimePathsWith(getenv, func() (string, error) {
		return "/tmp/workspace", nil
	})
	if err != nil {
		t.Fatalf("resolveRuntimePathsWith returned error: %v", err)
	}

	if paths.ServerHome != "/srv/bbs" {
		t.Fatalf("ServerHome = %q, want %q", paths.ServerHome, "/srv/bbs")
	}
	if paths.ConfigDir != "/etc/bbs" {
		t.Fatalf("ConfigDir = %q, want %q", paths.ConfigDir, "/etc/bbs")
	}
	if paths.DataDir != "/var/lib/bbs" {
		t.Fatalf("DataDir = %q, want %q", paths.DataDir, "/var/lib/bbs")
	}
	if paths.TemplatesDir != "/opt/bbs/templates" {
		t.Fatalf("TemplatesDir = %q, want %q", paths.TemplatesDir, "/opt/bbs/templates")
	}
	if paths.PluginsDir != "/opt/bbs/plugins/games" {
		t.Fatalf("PluginsDir = %q, want %q", paths.PluginsDir, "/opt/bbs/plugins/games")
	}
	if paths.SQLitePath != "/var/lib/bbs/state.db" {
		t.Fatalf("SQLitePath = %q, want %q", paths.SQLitePath, "/var/lib/bbs/state.db")
	}
}

func TestResolveRuntimePathsWith_LinuxFHSPackagingMode(t *testing.T) {
	getenv := func(key string) string {
		if key == packagingModeEnv {
			return "linux-fhs"
		}
		return ""
	}

	paths, err := resolveRuntimePathsWith(getenv, func() (string, error) {
		return "/tmp/workspace", nil
	})
	if err != nil {
		t.Fatalf("resolveRuntimePathsWith returned error: %v", err)
	}

	if paths.ServerHome != "/opt/bbs" {
		t.Fatalf("ServerHome = %q, want %q", paths.ServerHome, "/opt/bbs")
	}
	if paths.ConfigDir != "/etc/bbs" {
		t.Fatalf("ConfigDir = %q, want %q", paths.ConfigDir, "/etc/bbs")
	}
	if paths.DataDir != "/var/lib/bbs" {
		t.Fatalf("DataDir = %q, want %q", paths.DataDir, "/var/lib/bbs")
	}
	if paths.SQLitePath != "/var/lib/bbs/bbs.sqlite3" {
		t.Fatalf("SQLitePath = %q, want %q", paths.SQLitePath, "/var/lib/bbs/bbs.sqlite3")
	}
}

func TestResolveRuntimePathsWith_LinuxFHSWithEnvOverrides(t *testing.T) {
	getenv := func(key string) string {
		overrides := map[string]string{
			packagingModeEnv:   "linux-fhs",
			serverHomeEnv:      "/app/bbs",
			serverConfigDirEnv: "/etc/myapp",
		}
		return overrides[key]
	}

	paths, err := resolveRuntimePathsWith(getenv, func() (string, error) {
		return "/tmp/workspace", nil
	})
	if err != nil {
		t.Fatalf("resolveRuntimePathsWith returned error: %v", err)
	}

	if paths.ServerHome != "/app/bbs" {
		t.Fatalf("ServerHome = %q, want %q", paths.ServerHome, "/app/bbs")
	}
	if paths.ConfigDir != "/etc/myapp" {
		t.Fatalf("ConfigDir = %q, want %q", paths.ConfigDir, "/etc/myapp")
	}
	if paths.DataDir != "/var/lib/bbs" {
		t.Fatalf("DataDir (still FHS default despite ServerHome override) = %q, want %q", paths.DataDir, "/var/lib/bbs")
	}
}
