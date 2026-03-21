package main

import (
	"sync"
	"testing"
)

func TestNormalizeReleaseVersion(t *testing.T) {
	tests := []struct {
		name string
		raw  string
		want string
	}{
		{name: "empty", raw: "", want: ""},
		{name: "devel", raw: "(devel)", want: ""},
		{name: "plain semver", raw: "1.2.3", want: "v1.2.3"},
		{name: "already prefixed", raw: "v1.2.3", want: "v1.2.3"},
		{name: "with suffix", raw: "v1.2.3-beta.1", want: "v1.2.3"},
		{name: "invalid", raw: "release_candidate", want: ""},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := normalizeReleaseVersion(tc.raw)
			if got != tc.want {
				t.Fatalf("normalizeReleaseVersion(%q) = %q, want %q", tc.raw, got, tc.want)
			}
		})
	}
}

func TestResolveDashboardVersion_PrefersBuildVersion(t *testing.T) {
	oldBuild := buildVersion
	defer func() { buildVersion = oldBuild }()

	buildVersion = "v9.8.7-custom"
	got := resolveDashboardVersion()
	if got != "v9.8.7" {
		t.Fatalf("resolveDashboardVersion() = %q, want %q", got, "v9.8.7")
	}
}

func TestCurrentDashboardVersion_CachesValue(t *testing.T) {
	oldBuild := buildVersion
	oldOnce := dashboardVersionOnce
	oldValue := dashboardVersion
	defer func() {
		buildVersion = oldBuild
		dashboardVersionOnce = oldOnce
		dashboardVersion = oldValue
	}()

	dashboardVersionOnce = sync.Once{}
	dashboardVersion = ""

	buildVersion = "v1.2.3"
	first := currentDashboardVersion()
	if first != "v1.2.3" {
		t.Fatalf("first currentDashboardVersion() = %q, want %q", first, "v1.2.3")
	}

	buildVersion = "v9.9.9"
	second := currentDashboardVersion()
	if second != "v1.2.3" {
		t.Fatalf("second currentDashboardVersion() = %q, want cached %q", second, "v1.2.3")
	}
}
