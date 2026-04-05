package main

import (
	"strings"
	"testing"
)

func TestParseServerAddress_Valid(t *testing.T) {
	tests := []struct {
		name     string
		in       string
		wantHost string
		wantPort int
	}{
		{name: "localhost", in: "localhost:8080", wantHost: "localhost", wantPort: 8080},
		{name: "ipv4", in: "127.0.0.1:1", wantHost: "127.0.0.1", wantPort: 1},
		{name: "ipv6", in: "[::1]:443", wantHost: "::1", wantPort: 443},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			host, port, err := parseServerAddress(tc.in)
			if err != nil {
				t.Fatalf("parseServerAddress(%q) returned unexpected error: %v", tc.in, err)
			}
			if host != tc.wantHost || port != tc.wantPort {
				t.Fatalf("parseServerAddress(%q) = (%q, %d), want (%q, %d)", tc.in, host, port, tc.wantHost, tc.wantPort)
			}
		})
	}
}

func TestParseServerAddress_Invalid(t *testing.T) {
	tests := []struct {
		name string
		in   string
	}{
		{name: "empty", in: ""},
		{name: "missing port", in: "localhost"},
		{name: "missing host", in: ":8080"},
		{name: "non numeric port", in: "localhost:abc"},
		{name: "port out of range", in: "localhost:70000"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			_, _, err := parseServerAddress(tc.in)
			if err == nil {
				t.Fatalf("parseServerAddress(%q) expected error", tc.in)
			}
		})
	}
}

func TestParseLocalEndpoint_ValidUnixForms(t *testing.T) {
	tests := []struct {
		name        string
		in          string
		wantNetwork string
		wantAddress string
		wantDisplay string
	}{
		{name: "bare path", in: "/tmp/bbs-agent.sock", wantNetwork: "unix", wantAddress: "/tmp/bbs-agent.sock", wantDisplay: "unix:///tmp/bbs-agent.sock"},
		{name: "unix scheme", in: "unix:///tmp/bbs-agent.sock", wantNetwork: "unix", wantAddress: "/tmp/bbs-agent.sock", wantDisplay: "unix:///tmp/bbs-agent.sock"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			network, address, display, err := parseLocalEndpoint(tc.in)
			if err != nil {
				t.Fatalf("parseLocalEndpoint(%q) returned unexpected error: %v", tc.in, err)
			}
			if network != tc.wantNetwork || address != tc.wantAddress || display != tc.wantDisplay {
				t.Fatalf("parseLocalEndpoint(%q) = (%q, %q, %q), want (%q, %q, %q)", tc.in, network, address, display, tc.wantNetwork, tc.wantAddress, tc.wantDisplay)
			}
		})
	}
}

func TestParseLocalEndpoint_Invalid(t *testing.T) {
	tests := []struct {
		name string
		in   string
	}{
		{name: "empty", in: ""},
		{name: "unix no path", in: "unix://"},
		{name: "unsupported scheme", in: "tcp://127.0.0.1:9999"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			_, _, _, err := parseLocalEndpoint(tc.in)
			if err == nil {
				t.Fatalf("parseLocalEndpoint(%q) expected error", tc.in)
			}
		})
	}
}

func TestDefaultControlEndpoint(t *testing.T) {
	tests := []struct {
		name string
		in   string
		want string
	}{
		{name: "bare path", in: "/tmp/bbs-agent.sock", want: "unix:///tmp/bbs-agent.sock.control"},
		{name: "unix scheme", in: "unix:///tmp/bbs-agent.sock", want: "unix:///tmp/bbs-agent.sock.control"},
		{name: "empty", in: "", want: "unix:///tmp/bbs-agent-control.sock"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := defaultControlEndpoint(tc.in)
			if got != tc.want {
				t.Fatalf("defaultControlEndpoint(%q) = %q, want %q", tc.in, got, tc.want)
			}
		})
	}
}

func TestParseLocalHello_Valid(t *testing.T) {
	line := `{"v":"0.2","type":"hello","payload":{"name":"bot_one","owner_token":"owner_1","capabilities":["a"," b "],"credentials_file":"/tmp/creds.txt","bot_id":"bot_123","bot_secret":"sec_123"}}`

	hello, err := parseLocalHello(line)
	if err != nil {
		t.Fatalf("parseLocalHello returned unexpected error: %v", err)
	}

	if hello.Name != "bot_one" {
		t.Fatalf("hello.Name = %q, want %q", hello.Name, "bot_one")
	}
	if hello.OwnerToken != "owner_1" {
		t.Fatalf("hello.OwnerToken = %q, want %q", hello.OwnerToken, "owner_1")
	}
	if hello.CapabilitiesCSV != "a,b" {
		t.Fatalf("hello.CapabilitiesCSV = %q, want %q", hello.CapabilitiesCSV, "a,b")
	}
	if hello.CredentialsFile != "/tmp/creds.txt" {
		t.Fatalf("hello.CredentialsFile = %q, want %q", hello.CredentialsFile, "/tmp/creds.txt")
	}
	if hello.BotID != "bot_123" || hello.BotSecret != "sec_123" {
		t.Fatalf("unexpected credentials in hello: %+v", hello)
	}
}

func TestParseLocalHello_DefaultsAndErrors(t *testing.T) {
	t.Run("defaults", func(t *testing.T) {
		line := `{"v":"0.2","type":"hello","payload":{}}`
		hello, err := parseLocalHello(line)
		if err != nil {
			t.Fatalf("parseLocalHello returned unexpected error: %v", err)
		}
		if hello.Name != "agent_bot" {
			t.Fatalf("hello.Name = %q, want %q", hello.Name, "agent_bot")
		}
		if hello.CapabilitiesCSV != "any" {
			t.Fatalf("hello.CapabilitiesCSV = %q, want %q", hello.CapabilitiesCSV, "any")
		}
	})

	t.Run("invalid version", func(t *testing.T) {
		line := `{"v":"0.1","type":"hello","payload":{}}`
		_, err := parseLocalHello(line)
		if err == nil {
			t.Fatal("expected error for unsupported version")
		}
		if !strings.Contains(err.Error(), "unsupported hello version") {
			t.Fatalf("unexpected error: %v", err)
		}
	})

	t.Run("invalid type", func(t *testing.T) {
		line := `{"v":"0.2","type":"turn","payload":{}}`
		_, err := parseLocalHello(line)
		if err == nil {
			t.Fatal("expected error for invalid first message type")
		}
		if !strings.Contains(err.Error(), "must be type=hello") {
			t.Fatalf("unexpected error: %v", err)
		}
	})

	t.Run("invalid json", func(t *testing.T) {
		_, err := parseLocalHello("not json")
		if err == nil {
			t.Fatal("expected error for invalid json")
		}
	})
}

func TestBuildRegisterCommand(t *testing.T) {
	t.Run("empty credentials", func(t *testing.T) {
		cmd := buildRegisterCommand("bot_one", credentials{}, "", "", "", "")
		want := `REGISTER bot_one`
		if cmd != want {
			t.Fatalf("buildRegisterCommand = %q, want %q", cmd, want)
		}
	})

	t.Run("full command", func(t *testing.T) {
		cmd := buildRegisterCommand(" bot_one ", credentials{BotID: " bot_1 ", BotSecret: " sec_1 "}, "a,b", "owner_1", "nonce_abc", "1712000")
		want := `REGISTER bot_one a,b owner_token=owner_1 client_nonce=nonce_abc client_ts=1712000`
		if cmd != want {
			t.Fatalf("buildRegisterCommand = %q, want %q", cmd, want)
		}
	})
}

func TestSplitCapabilities(t *testing.T) {
	got := splitCapabilities("a, b, ,a , c")
	want := []string{"a", "b", "a", "c"}

	if len(got) != len(want) {
		t.Fatalf("splitCapabilities length = %d, want %d (%v)", len(got), len(want), got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("splitCapabilities[%d] = %q, want %q", i, got[i], want[i])
		}
	}
}
