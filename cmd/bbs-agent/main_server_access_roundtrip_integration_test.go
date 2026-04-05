package main

import (
	"bufio"
	"context"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestServerAccessRoundTrip_WithRealServer(t *testing.T) {
	t.Helper()

	if testing.Short() {
		t.Skip("skipping integration test in short mode")
	}

	stadiumPort, err := reserveTCPPort()
	if err != nil {
		t.Fatalf("reserveTCPPort(stadium) failed: %v", err)
	}
	dashPort, err := reserveTCPPort()
	if err != nil {
		t.Fatalf("reserveTCPPort(dash) failed: %v", err)
	}

	repoRoot, err := filepath.Abs(filepath.Join("..", ".."))
	if err != nil {
		t.Fatalf("failed to resolve repo root: %v", err)
	}

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	serverCmd := exec.CommandContext(ctx, "go", "run", "./cmd/bbs-server", "--stadium", stadiumPort, "--dash", dashPort)
	serverCmd.Dir = repoRoot
	serverCmd.Env = append(os.Environ(),
		"BBS_DATA_DIR="+filepath.Join(t.TempDir(), "data"),
		"BBS_ENABLE_MOCK_GLOBAL_REGISTRY=true",
	)
	serverLog := &strings.Builder{}
	serverCmd.Stdout = serverLog
	serverCmd.Stderr = serverLog

	if err := serverCmd.Start(); err != nil {
		t.Fatalf("failed to start bbs-server: %v", err)
	}
	defer func() {
		_ = serverCmd.Process.Kill()
		_, _ = serverCmd.Process.Wait()
	}()

	serverEndpoint := net.JoinHostPort("127.0.0.1", stadiumPort)
	if err := waitForTCPServer(serverEndpoint, 8*time.Second); err != nil {
		t.Fatalf("bbs-server did not become ready: %v\nserver log:\n%s", err, serverLog.String())
	}

	ag, err := newAgent(runtimeConfig{server: serverEndpoint, name: "agent_bot", capabilities: "any"})
	if err != nil {
		t.Fatalf("newAgent failed: %v", err)
	}
	defer ag.shutdown("test_done")

	ag.name = "agent_bot"
	ag.capabilities = "any"

	controlSock := filepath.Join(t.TempDir(), "control.sock")
	if err := ag.startControlListener("unix://" + controlSock); err != nil {
		t.Fatalf("startControlListener failed: %v", err)
	}

	controlConn, err := net.DialTimeout("unix", controlSock, 2*time.Second)
	if err != nil {
		t.Fatalf("failed to connect control socket: %v", err)
	}
	defer controlConn.Close()

	controlScanner := bufio.NewScanner(controlConn)
	controlScanner.Buffer(make([]byte, 0, 1024), maxScannerToken)
	_ = readControlMessage(t, controlScanner)

	if err := ag.connectServer(); err != nil {
		t.Fatalf("connectServer failed: %v\nserver log:\n%s", err, serverLog.String())
	}

	registerCommand := buildRegisterCommand(ag.name, credentials{}, ag.capabilities, "", "nonce_test", "1712000")
	if err := ag.sendServerCommand(registerCommand); err != nil {
		t.Fatalf("sendServerCommand(REGISTER) failed: %v", err)
	}

	registerMsg, err := waitForRegister(ag.registerCh, 6*time.Second)
	if err != nil {
		t.Fatalf("waitForRegister failed: %v\nserver log:\n%s", err, serverLog.String())
	}
	if strings.ToLower(strings.TrimSpace(registerMsg.Status)) != "ok" {
		t.Fatalf("register status = %q, want ok; payload=%#v", registerMsg.Status, registerMsg.Payload)
	}

	payload, ok := registerMsg.Payload.(map[string]interface{})
	if !ok {
		t.Fatalf("register payload has unexpected type: %T", registerMsg.Payload)
	}
	ag.applyRegisterPayload(payload)

	writeControlMessage(t, controlConn, contractMessage{V: contractVersion, Type: "server_access", ID: "access-1", Payload: map[string]interface{}{}})
	accessResp := readControlMessage(t, controlScanner)
	if accessResp.Type != "server_access" {
		t.Fatalf("server_access response type = %q, want server_access", accessResp.Type)
	}
	if accessResp.ID != "access-1" {
		t.Fatalf("server_access response id = %q, want access-1", accessResp.ID)
	}

	accessPayload, ok := accessResp.Payload.(map[string]interface{})
	if !ok {
		t.Fatalf("server_access payload has unexpected type: %T", accessResp.Payload)
	}

	ownerToken := asString(accessPayload["owner_token"])
	if ownerToken == "" {
		t.Fatalf("owner_token is empty in server_access payload: %#v", accessPayload)
	}

	endpoint := asString(accessPayload["dashboard_endpoint"])
	if endpoint == "" {
		t.Fatalf("dashboard_endpoint is empty in server_access payload: %#v", accessPayload)
	}

	host, port, splitErr := net.SplitHostPort(endpoint)
	if splitErr != nil {
		t.Fatalf("dashboard_endpoint %q is not host:port: %v", endpoint, splitErr)
	}
	if strings.TrimSpace(host) == "" {
		t.Fatalf("dashboard_endpoint host is empty: %q", endpoint)
	}
	if port != dashPort {
		t.Fatalf("dashboard_endpoint port = %q, want %q", port, dashPort)
	}
}

func reserveTCPPort() (string, error) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return "", err
	}
	defer ln.Close()
	_, port, err := net.SplitHostPort(ln.Addr().String())
	if err != nil {
		return "", err
	}
	return port, nil
}

func waitForTCPServer(endpoint string, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		conn, err := net.DialTimeout("tcp", endpoint, 300*time.Millisecond)
		if err == nil {
			_ = conn.Close()
			return nil
		}
		time.Sleep(100 * time.Millisecond)
	}
	return fmt.Errorf("timeout waiting for tcp endpoint %s", endpoint)
}
