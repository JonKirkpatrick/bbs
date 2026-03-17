package main

import (
	"os/exec"
	"regexp"
	"runtime/debug"
	"strings"
	"sync"
)

// buildVersion can be set at build time via:
// go build -ldflags "-X main.buildVersion=v0.0.2" ...
var buildVersion string

var (
	dashboardVersionOnce sync.Once
	dashboardVersion     string
	semverPrefixPattern  = regexp.MustCompile(`^v?(\d+)\.(\d+)\.(\d+)`)
)

func currentDashboardVersion() string {
	dashboardVersionOnce.Do(func() {
		dashboardVersion = resolveDashboardVersion()
	})
	return dashboardVersion
}

func resolveDashboardVersion() string {
	candidates := []string{
		buildVersion,
		buildInfoVersion(),
		latestReleaseTagFromGit(),
	}

	for _, candidate := range candidates {
		if normalized := normalizeReleaseVersion(candidate); normalized != "" {
			return normalized
		}
	}

	return "unreleased"
}

func buildInfoVersion() string {
	info, ok := debug.ReadBuildInfo()
	if !ok {
		return ""
	}
	return strings.TrimSpace(info.Main.Version)
}

func latestReleaseTagFromGit() string {
	out, err := exec.Command("git", "describe", "--tags", "--abbrev=0", "--match", "v[0-9]*").Output()
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(out))
}

func normalizeReleaseVersion(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" || raw == "(devel)" {
		return ""
	}

	match := semverPrefixPattern.FindStringSubmatch(raw)
	if len(match) != 4 {
		return ""
	}

	return "v" + match[1] + "." + match[2] + "." + match[3]
}
