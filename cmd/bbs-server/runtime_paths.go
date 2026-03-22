package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

const (
	serverHomeEnv         = "BBS_SERVER_HOME"
	serverConfigDirEnv    = "BBS_CONFIG_DIR"
	serverDataDirEnv      = "BBS_DATA_DIR"
	templateDirEnv        = "BBS_TEMPLATE_DIR"
	pluginDirEnv          = "BBS_GAME_PLUGIN_DIR"
	packagingModeEnv      = "BBS_PACKAGING_MODE"
	packagingModeLinuxFHS = "linux-fhs"
)

type runtimePaths struct {
	ServerHome   string
	ConfigDir    string
	DataDir      string
	TemplatesDir string
	PluginsDir   string
	SQLitePath   string
}

var (
	runtimePathsMu    sync.RWMutex
	cachedRuntimePath runtimePaths
)

func setRuntimePaths(paths runtimePaths) {
	runtimePathsMu.Lock()
	defer runtimePathsMu.Unlock()
	cachedRuntimePath = paths
}

func getRuntimePaths() (runtimePaths, error) {
	runtimePathsMu.RLock()
	cached := cachedRuntimePath
	runtimePathsMu.RUnlock()
	if cached.SQLitePath != "" {
		return cached, nil
	}

	resolved, err := resolveRuntimePaths()
	if err != nil {
		return runtimePaths{}, err
	}
	setRuntimePaths(resolved)
	return resolved, nil
}

func resolveRuntimePaths() (runtimePaths, error) {
	return resolveRuntimePathsWith(os.Getenv, os.Getwd)
}

func resolveRuntimePathsWith(getenv func(string) string, getwd func() (string, error)) (runtimePaths, error) {
	cwd, err := getwd()
	if err != nil {
		return runtimePaths{}, fmt.Errorf("resolve working directory: %w", err)
	}
	cwd = filepath.Clean(cwd)

	packagingMode := strings.ToLower(strings.TrimSpace(getenv(packagingModeEnv)))
	isLinuxFHS := packagingMode == packagingModeLinuxFHS

	var serverHomeDefault string
	if isLinuxFHS {
		serverHomeDefault = "/opt/bbs"
	} else {
		serverHomeDefault = cwd
	}

	serverHome, err := resolvePath(cwd, firstNonEmpty(strings.TrimSpace(getenv(serverHomeEnv)), serverHomeDefault))
	if err != nil {
		return runtimePaths{}, fmt.Errorf("resolve %s: %w", serverHomeEnv, err)
	}

	var configDirDefault string
	if isLinuxFHS {
		configDirDefault = "/etc/bbs"
	} else {
		configDirDefault = filepath.Join(serverHome, "config")
	}

	configDir, err := resolvePath(cwd, firstNonEmpty(strings.TrimSpace(getenv(serverConfigDirEnv)), configDirDefault))
	if err != nil {
		return runtimePaths{}, fmt.Errorf("resolve %s: %w", serverConfigDirEnv, err)
	}

	var dataDirDefault string
	if isLinuxFHS {
		dataDirDefault = "/var/lib/bbs"
	} else {
		dataDirDefault = filepath.Join(serverHome, "data")
	}

	dataDir, err := resolvePath(cwd, firstNonEmpty(strings.TrimSpace(getenv(serverDataDirEnv)), dataDirDefault))
	if err != nil {
		return runtimePaths{}, fmt.Errorf("resolve %s: %w", serverDataDirEnv, err)
	}

	var templatesDefault string
	if isLinuxFHS {
		templatesDefault = detectFirstExistingDir(
			"/opt/bbs/templates",
			"/usr/lib/bbs/templates",
		)
		if templatesDefault == "" {
			templatesDefault = "/opt/bbs/templates"
		}
	} else {
		templatesDefault = detectFirstExistingDir(
			filepath.Join(cwd, "templates"),
			filepath.Join(cwd, "cmd", "bbs-server", "templates"),
			filepath.Join(serverHome, "templates"),
			filepath.Join(serverHome, "cmd", "bbs-server", "templates"),
		)
		if templatesDefault == "" {
			templatesDefault = filepath.Join(serverHome, "templates")
		}
	}
	templatesDir, err := resolvePath(cwd, firstNonEmpty(strings.TrimSpace(getenv(templateDirEnv)), templatesDefault))
	if err != nil {
		return runtimePaths{}, fmt.Errorf("resolve %s: %w", templateDirEnv, err)
	}

	var pluginsDefault string
	if isLinuxFHS {
		pluginsDefault = detectFirstExistingDir(
			"/var/lib/bbs/plugins/games",
			"/usr/lib/bbs/plugins/games",
		)
		if pluginsDefault == "" {
			pluginsDefault = "/var/lib/bbs/plugins/games"
		}
	} else {
		pluginsDefault = detectFirstExistingDir(
			filepath.Join(cwd, "plugins", "games"),
			filepath.Join(cwd, "cmd", "bbs-server", "plugins", "games"),
			filepath.Join(serverHome, "plugins", "games"),
			filepath.Join(serverHome, "cmd", "bbs-server", "plugins", "games"),
		)
		if pluginsDefault == "" {
			pluginsDefault = filepath.Join(serverHome, "plugins", "games")
		}
	}
	pluginsDir, err := resolvePath(cwd, firstNonEmpty(strings.TrimSpace(getenv(pluginDirEnv)), pluginsDefault))
	if err != nil {
		return runtimePaths{}, fmt.Errorf("resolve %s: %w", pluginDirEnv, err)
	}

	sqlitePath, err := resolvePath(cwd, firstNonEmpty(strings.TrimSpace(getenv("BBS_SQLITE_PATH")), filepath.Join(dataDir, "bbs.sqlite3")))
	if err != nil {
		return runtimePaths{}, fmt.Errorf("resolve BBS_SQLITE_PATH: %w", err)
	}

	return runtimePaths{
		ServerHome:   filepath.Clean(serverHome),
		ConfigDir:    filepath.Clean(configDir),
		DataDir:      filepath.Clean(dataDir),
		TemplatesDir: filepath.Clean(templatesDir),
		PluginsDir:   filepath.Clean(pluginsDir),
		SQLitePath:   filepath.Clean(sqlitePath),
	}, nil
}

func resolvePath(base, candidate string) (string, error) {
	if candidate == "" {
		return "", fmt.Errorf("path is empty")
	}
	if filepath.IsAbs(candidate) {
		return filepath.Clean(candidate), nil
	}
	if base == "" {
		base = "."
	}
	return filepath.Abs(filepath.Join(base, candidate))
}

func detectFirstExistingDir(candidates ...string) string {
	for _, candidate := range candidates {
		if candidate == "" {
			continue
		}
		info, err := os.Stat(candidate)
		if err == nil && info.IsDir() {
			return candidate
		}
	}
	return ""
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return value
		}
	}
	return ""
}
