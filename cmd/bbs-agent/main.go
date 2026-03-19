package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"os/signal"
	"regexp"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

const (
	contractVersion = "0.2"
	agentVersion    = "0.2.0"
	maxScannerToken = 16 * 1024 * 1024
)

type credentials struct {
	BotID     string
	BotSecret string
}

type contractMessage struct {
	V       string      `json:"v"`
	Type    string      `json:"type"`
	ID      string      `json:"id,omitempty"`
	Payload interface{} `json:"payload"`
}

type serverMessage struct {
	Status  string      `json:"status"`
	Type    string      `json:"type"`
	Payload interface{} `json:"payload"`
}

type botEnvelope struct {
	V       string          `json:"v"`
	Type    string          `json:"type"`
	ID      string          `json:"id,omitempty"`
	Payload json.RawMessage `json:"payload"`
}

type localHello struct {
	Name            string
	OwnerToken      string
	CapabilitiesCSV string
	CredentialsFile string
	BotID           string
	BotSecret       string
}

type agent struct {
	ctx    context.Context
	cancel context.CancelFunc

	name         string
	server       string
	ownerToken   string
	capabilities string

	botConn net.Conn
	botMu   sync.Mutex

	conn     net.Conn
	serverMu sync.Mutex

	localListener   net.Listener
	localSocketPath string

	registerCh   chan serverMessage
	serverErrCh  chan error
	botReadErrCh chan error

	sessionID int

	joinedArenaID  int
	joinedPlayerID int
	joinedGame     string
	joinedTimeMS   int
	joinedMoveMS   int
	turnStep       int

	lastStatePayload map[string]interface{}
	pendingResponse  map[string]interface{}
	awaitingAction   atomic.Bool
}

func main() {
	os.Exit(run())
}

func run() int {
	cfg, err := parseFlags()
	if err != nil {
		fmt.Fprintf(os.Stderr, "[agent] %v\n", err)
		return 1
	}

	ag, err := newAgent(cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[agent] failed to initialize: %v\n", err)
		return 1
	}
	defer ag.shutdown("agent_exit")

	var creds credentials
	hello, scanner, conn, err := ag.acceptLocalConnection(cfg.listen)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[agent] failed to start local bridge: %v\n", err)
		return 1
	}

	applyLocalHello(&cfg, &creds, hello)
	if strings.Contains(cfg.name, " ") {
		fmt.Fprintf(os.Stderr, "[agent] local hello rejected: name cannot contain spaces\n")
		return 1
	}
	if cfg.credentialsFile == "" {
		cfg.credentialsFile = defaultCredentialsFilePath(cfg.name)
	}
	cfg.credentialsFile = strings.TrimSpace(cfg.credentialsFile)

	fileCreds, err := loadCredentials(cfg.credentialsFile)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[agent] failed to load credentials: %v\n", err)
		return 1
	}
	if strings.TrimSpace(creds.BotID) == "" {
		creds.BotID = fileCreds.BotID
	}
	if strings.TrimSpace(creds.BotSecret) == "" {
		creds.BotSecret = fileCreds.BotSecret
	}

	ag.name = cfg.name
	ag.ownerToken = strings.TrimSpace(cfg.ownerToken)
	ag.capabilities = strings.TrimSpace(cfg.capabilities)
	ag.botConn = conn
	go ag.readBotScanner(scanner)

	if err := ag.connectServer(); err != nil {
		fmt.Fprintf(os.Stderr, "[agent] server connect failed: %v\n", err)
		return 1
	}

	registerCommand := buildRegisterCommand(cfg.name, creds, cfg.capabilities, cfg.ownerToken)
	fmt.Fprintf(os.Stderr, "[agent] sending REGISTER to %s\n", cfg.server)
	if err := ag.sendServerCommand(registerCommand); err != nil {
		fmt.Fprintf(os.Stderr, "[agent] failed to send register command: %v\n", err)
		return 1
	}

	registerMsg, err := waitForRegister(ag.registerCh, cfg.registerTimeout)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[agent] register failed: %v\n", err)
		return 1
	}

	if strings.ToLower(strings.TrimSpace(registerMsg.Status)) != "ok" {
		fmt.Fprintf(os.Stderr, "[agent] register rejected: %v\n", registerMsg.Payload)
		return 1
	}

	registerPayload, _ := registerMsg.Payload.(map[string]interface{})
	ag.sessionID = asInt(registerPayload["session_id"])
	if registerPayload != nil {
		botID := asString(registerPayload["bot_id"])
		botSecret := asString(registerPayload["bot_secret"])
		if botID != "" && botSecret != "" {
			if err := saveCredentials(cfg.credentialsFile, credentials{BotID: botID, BotSecret: botSecret}); err != nil {
				fmt.Fprintf(os.Stderr, "[agent] warning: failed to save credentials: %v\n", err)
			} else {
				fmt.Fprintf(os.Stderr, "[agent] saved credentials to %s\n", cfg.credentialsFile)
			}
		}
	}

	fmt.Fprintln(os.Stderr, "[agent] registered; waiting for JOIN to send bot welcome")

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, os.Interrupt, syscall.SIGTERM)
	defer signal.Stop(sigCh)

	select {
	case sig := <-sigCh:
		fmt.Fprintf(os.Stderr, "[agent] signal received: %s\n", sig)
	case err := <-ag.serverErrCh:
		if err != nil && !errors.Is(err, io.EOF) {
			fmt.Fprintf(os.Stderr, "[agent] server reader stopped: %v\n", err)
		}
	case err := <-ag.botReadErrCh:
		if err != nil && !errors.Is(err, io.EOF) {
			fmt.Fprintf(os.Stderr, "[agent] bot reader stopped: %v\n", err)
		}
	}

	return 0
}

type runtimeConfig struct {
	server          string
	listen          string
	name            string
	ownerToken      string
	capabilities    string
	credentialsFile string
	registerTimeout time.Duration
}

func parseFlags() (runtimeConfig, error) {
	var cfg runtimeConfig

	flag.StringVar(&cfg.server, "server", "", "BBS server endpoint in host:port format")
	flag.StringVar(&cfg.listen, "listen", "", "local endpoint for bot bridge (linux/mac: unix:///tmp/bbs-agent.sock or /tmp/bbs-agent.sock)")
	flag.StringVar(&cfg.name, "name", "agent_bot", "bot display name used during REGISTER")
	flag.StringVar(&cfg.ownerToken, "owner-token", "", "optional owner token from dashboard")
	flag.StringVar(&cfg.capabilities, "capabilities", "any", "comma-separated capability list")
	flag.StringVar(&cfg.credentialsFile, "credentials-file", "", "path to bot credentials file (key=value format)")
	flag.DurationVar(&cfg.registerTimeout, "register-timeout", 12*time.Second, "server register response timeout")

	flag.Parse()

	if _, _, err := parseServerAddress(cfg.server); err != nil {
		return cfg, err
	}
	cfg.listen = strings.TrimSpace(cfg.listen)
	if cfg.listen == "" {
		return cfg, errors.New("--listen is required")
	}
	if strings.Contains(cfg.name, " ") {
		return cfg, errors.New("--name cannot contain spaces")
	}

	return cfg, nil
}

func newAgent(cfg runtimeConfig) (*agent, error) {
	ctx, cancel := context.WithCancel(context.Background())
	return &agent{
		ctx:          ctx,
		cancel:       cancel,
		name:         cfg.name,
		server:       cfg.server,
		ownerToken:   strings.TrimSpace(cfg.ownerToken),
		capabilities: strings.TrimSpace(cfg.capabilities),
		registerCh:   make(chan serverMessage, 1),
		serverErrCh:  make(chan error, 1),
		botReadErrCh: make(chan error, 1),
	}, nil
}

func (a *agent) acceptLocalConnection(rawEndpoint string) (localHello, *bufio.Scanner, net.Conn, error) {
	network, address, display, err := parseLocalEndpoint(rawEndpoint)
	if err != nil {
		return localHello{}, nil, nil, err
	}

	if network == "unix" {
		_ = os.Remove(address)
		a.localSocketPath = address
	}

	listener, err := net.Listen(network, address)
	if err != nil {
		return localHello{}, nil, nil, err
	}
	a.localListener = listener

	fmt.Fprintf(os.Stderr, "[agent] local bridge listening on %s\n", display)

	conn, err := listener.Accept()
	if err != nil {
		return localHello{}, nil, nil, err
	}

	scanner := bufio.NewScanner(conn)
	buf := make([]byte, 0, 64*1024)
	scanner.Buffer(buf, maxScannerToken)
	if !scanner.Scan() {
		if scanErr := scanner.Err(); scanErr != nil {
			return localHello{}, nil, nil, scanErr
		}
		return localHello{}, nil, nil, errors.New("local client disconnected before hello")
	}

	hello, err := parseLocalHello(scanner.Text())
	if err != nil {
		return localHello{}, nil, nil, err
	}

	fmt.Fprintf(os.Stderr, "[agent] local bot connected; name=%s capabilities=%s\n", hello.Name, hello.CapabilitiesCSV)
	return hello, scanner, conn, nil
}

func parseLocalEndpoint(raw string) (network string, address string, display string, err error) {
	endpoint := strings.TrimSpace(raw)
	if endpoint == "" {
		return "", "", "", errors.New("--listen endpoint is empty")
	}

	if strings.HasPrefix(endpoint, "unix://") {
		address = strings.TrimSpace(strings.TrimPrefix(endpoint, "unix://"))
		if address == "" {
			return "", "", "", errors.New("invalid --listen unix endpoint")
		}
		return "unix", address, "unix://" + address, nil
	}

	if strings.Contains(endpoint, "://") {
		return "", "", "", fmt.Errorf("unsupported --listen endpoint %q (expected unix:///path/to.sock)", raw)
	}

	return "unix", endpoint, "unix://" + endpoint, nil
}

func parseLocalHello(line string) (localHello, error) {
	var env botEnvelope
	if err := json.Unmarshal([]byte(strings.TrimSpace(line)), &env); err != nil {
		return localHello{}, fmt.Errorf("invalid hello JSON: %w", err)
	}
	if env.V != contractVersion {
		return localHello{}, fmt.Errorf("unsupported hello version %q", env.V)
	}
	if strings.ToLower(strings.TrimSpace(env.Type)) != "hello" {
		return localHello{}, fmt.Errorf("first local message must be type=hello (got %q)", env.Type)
	}

	var payload map[string]interface{}
	if err := json.Unmarshal(env.Payload, &payload); err != nil {
		return localHello{}, fmt.Errorf("invalid hello payload: %w", err)
	}

	out := localHello{
		Name:            asString(payload["name"]),
		OwnerToken:      asString(payload["owner_token"]),
		CapabilitiesCSV: capabilitiesCSV(payload["capabilities"]),
		CredentialsFile: asString(payload["credentials_file"]),
		BotID:           asString(payload["bot_id"]),
		BotSecret:       asString(payload["bot_secret"]),
	}
	if out.CapabilitiesCSV == "" {
		out.CapabilitiesCSV = asString(payload["capabilities_csv"])
	}
	if out.Name == "" {
		out.Name = "agent_bot"
	}
	if out.CapabilitiesCSV == "" {
		out.CapabilitiesCSV = "any"
	}

	return out, nil
}

func applyLocalHello(cfg *runtimeConfig, creds *credentials, hello localHello) {
	if hello.Name != "" {
		cfg.name = hello.Name
	}
	if hello.OwnerToken != "" {
		cfg.ownerToken = hello.OwnerToken
	}
	if hello.CapabilitiesCSV != "" {
		cfg.capabilities = hello.CapabilitiesCSV
	}
	if hello.CredentialsFile != "" {
		cfg.credentialsFile = hello.CredentialsFile
	}
	if hello.BotID != "" {
		creds.BotID = hello.BotID
	}
	if hello.BotSecret != "" {
		creds.BotSecret = hello.BotSecret
	}
}

func capabilitiesCSV(raw interface{}) string {
	switch val := raw.(type) {
	case string:
		return strings.TrimSpace(val)
	case []interface{}:
		parts := make([]string, 0, len(val))
		for _, item := range val {
			part := asString(item)
			if part == "" {
				continue
			}
			parts = append(parts, part)
		}
		return strings.Join(parts, ",")
	default:
		return ""
	}
}

func (a *agent) connectServer() error {
	conn, err := net.Dial("tcp", a.server)
	if err != nil {
		return err
	}
	a.conn = conn

	go a.readServer()
	return nil
}

func waitForRegister(ch <-chan serverMessage, timeout time.Duration) (serverMessage, error) {
	select {
	case msg := <-ch:
		return msg, nil
	case <-time.After(timeout):
		return serverMessage{}, fmt.Errorf("timeout waiting for register response after %s", timeout)
	}
}

func (a *agent) readBotScanner(scanner *bufio.Scanner) {
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		a.handleBotLine(line)
	}

	err := scanner.Err()
	select {
	case a.botReadErrCh <- err:
	default:
	}
}

func (a *agent) handleBotLine(line string) {
	var env botEnvelope
	if err := json.Unmarshal([]byte(line), &env); err != nil {
		fmt.Fprintf(os.Stderr, "[agent] invalid bot JSON: %s\n", line)
		return
	}

	if env.V != contractVersion {
		fmt.Fprintf(os.Stderr, "[agent] ignoring bot message with unsupported version: %s\n", env.V)
		return
	}

	typeName := strings.ToLower(strings.TrimSpace(env.Type))
	switch typeName {
	case "action":
		var payload struct {
			Action string `json:"action"`
		}
		if err := json.Unmarshal(env.Payload, &payload); err != nil {
			fmt.Fprintf(os.Stderr, "[agent] bot action payload parse error: %v\n", err)
			return
		}
		action := strings.TrimSpace(payload.Action)
		if action == "" {
			return
		}
		if !a.awaitingAction.CompareAndSwap(true, false) {
			fmt.Fprintln(os.Stderr, "[agent] ignoring action: no turn is currently pending")
			return
		}
		action = strings.ReplaceAll(action, "\n", "")
		action = strings.ReplaceAll(action, "\r", "")
		if err := a.sendServerCommand("MOVE " + action); err != nil {
			a.awaitingAction.Store(true)
			fmt.Fprintf(os.Stderr, "[agent] failed to forward action: %v\n", err)
		}
	case "log":
		var payload struct {
			Level   string `json:"level"`
			Message string `json:"message"`
		}
		if err := json.Unmarshal(env.Payload, &payload); err != nil {
			fmt.Fprintf(os.Stderr, "[agent] bot log payload parse error: %v\n", err)
			return
		}
		fmt.Fprintf(os.Stderr, "[bot:%s] %s\n", strings.TrimSpace(payload.Level), strings.TrimSpace(payload.Message))
	default:
		fmt.Fprintf(os.Stderr, "[agent] ignoring unsupported bot message type=%s (expected action/log)\n", env.Type)
	}
}

func (a *agent) readServer() {
	scanner := bufio.NewScanner(a.conn)
	buf := make([]byte, 0, 64*1024)
	scanner.Buffer(buf, maxScannerToken)

	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		a.handleServerLine(line)
	}

	err := scanner.Err()
	select {
	case a.serverErrCh <- err:
	default:
	}
}

func (a *agent) handleServerLine(line string) {
	var msg serverMessage
	if err := json.Unmarshal([]byte(line), &msg); err != nil {
		fmt.Fprintf(os.Stderr, "[agent] server text: %s\n", line)
		return
	}

	msgType := strings.ToLower(strings.TrimSpace(msg.Type))
	status := strings.ToLower(strings.TrimSpace(msg.Status))

	if msgType == "register" {
		select {
		case a.registerCh <- msg:
		default:
		}
		return
	}

	switch msgType {
	case "join":
		if payloadMap, ok := msg.Payload.(map[string]interface{}); ok {
			a.joinedArenaID = asInt(payloadMap["arena_id"])
			a.joinedPlayerID = asInt(payloadMap["player_id"])
			a.joinedGame = asString(payloadMap["game"])
			a.joinedTimeMS = asInt(payloadMap["time_limit_ms"])
			a.joinedMoveMS = asInt(payloadMap["effective_time_limit_ms"])
			if a.joinedMoveMS <= 0 {
				a.joinedMoveMS = a.joinedTimeMS
			}
			a.turnStep = 0
			a.pendingResponse = nil
			a.lastStatePayload = nil
			a.awaitingAction.Store(false)

			welcome := map[string]interface{}{
				"agent_name":              "bbs-agent",
				"agent_version":           agentVersion,
				"server":                  a.server,
				"session_id":              a.sessionID,
				"arena_id":                a.joinedArenaID,
				"player_id":               a.joinedPlayerID,
				"env":                     a.joinedGame,
				"time_limit_ms":           a.joinedTimeMS,
				"effective_time_limit_ms": a.joinedMoveMS,
				"capabilities":            splitCapabilities(a.capabilities),
			}
			_ = a.sendBot(contractMessage{V: contractVersion, Type: "welcome", Payload: welcome})
		}
	case "data":
		statePayload := buildStatePayload(msg.Payload, a.joinedPlayerID)
		a.lastStatePayload = statePayload
		if !shouldForwardTurn(statePayload, a.joinedPlayerID) {
			a.awaitingAction.Store(false)
			return
		}

		isDone := statePayloadDone(statePayload)
		reward := statePayloadReward(statePayload)

		a.turnStep++
		turnPayload := map[string]interface{}{
			"step":        a.turnStep,
			"deadline_ms": a.joinedMoveMS,
			"obs":         statePayload,
			"reward":      reward,
			"done":        isDone,
			"truncated":   false,
		}
		if a.pendingResponse != nil {
			turnPayload["response"] = a.pendingResponse
		}
		a.pendingResponse = nil
		a.awaitingAction.Store(!isDone)
		_ = a.sendBot(contractMessage{V: contractVersion, Type: "turn", Payload: turnPayload})
	case "move", "error", "timeout", "ejected":
		a.pendingResponse = map[string]interface{}{
			"type":    msgType,
			"status":  status,
			"payload": msg.Payload,
		}

		if a.lastStatePayload != nil && shouldForwardTurn(a.lastStatePayload, a.joinedPlayerID) && status == "err" {
			a.turnStep++
			turnPayload := map[string]interface{}{
				"step":        a.turnStep,
				"deadline_ms": a.joinedMoveMS,
				"obs":         a.lastStatePayload,
				"reward":      0.0,
				"done":        false,
				"truncated":   false,
				"response":    a.pendingResponse,
			}
			a.pendingResponse = nil
			a.awaitingAction.Store(true)
			_ = a.sendBot(contractMessage{V: contractVersion, Type: "turn", Payload: turnPayload})
		}

		if msgType == "timeout" || msgType == "ejected" {
			a.turnStep++
			terminalPayload := map[string]interface{}{
				"step":      a.turnStep,
				"reward":    terminalReward(msgType, msg.Payload, a.joinedPlayerID, status),
				"done":      true,
				"truncated": true,
				"response":  a.pendingResponse,
			}
			if a.lastStatePayload != nil {
				terminalPayload["obs"] = a.lastStatePayload
			}
			a.pendingResponse = nil
			a.awaitingAction.Store(false)
			_ = a.sendBot(contractMessage{V: contractVersion, Type: "turn", Payload: terminalPayload})
		}
	case "gameover":
		responsePayload := map[string]interface{}{
			"type":    msgType,
			"status":  status,
			"payload": msg.Payload,
		}
		if statePayloadDone(a.lastStatePayload) {
			// A terminal data frame was already forwarded for this turn.
			// Keep gameover metadata for the next forwarded turn (if any) and avoid double terminal events.
			a.pendingResponse = responsePayload
			a.awaitingAction.Store(false)
			return
		}

		a.turnStep++
		terminalPayload := map[string]interface{}{
			"step":      a.turnStep,
			"reward":    terminalReward(msgType, msg.Payload, a.joinedPlayerID, status),
			"done":      true,
			"truncated": false,
			"response":  responsePayload,
		}
		if a.lastStatePayload != nil {
			terminalPayload["obs"] = a.lastStatePayload
		}
		a.pendingResponse = nil
		a.awaitingAction.Store(false)
		_ = a.sendBot(contractMessage{V: contractVersion, Type: "turn", Payload: terminalPayload})
	case "episode_end":
		responsePayload := map[string]interface{}{
			"type":    msgType,
			"status":  status,
			"payload": msg.Payload,
		}
		if statePayloadDone(a.lastStatePayload) {
			// For episodic environments the terminal step already arrived via "data".
			// Preserve episode_end metadata but do not emit a duplicate done=true turn.
			a.pendingResponse = responsePayload
			a.awaitingAction.Store(false)
			return
		}

		a.turnStep++
		terminalPayload := map[string]interface{}{
			"step":      a.turnStep,
			"reward":    terminalReward(msgType, msg.Payload, a.joinedPlayerID, status),
			"done":      true,
			"truncated": false,
			"response":  responsePayload,
		}
		if a.lastStatePayload != nil {
			terminalPayload["obs"] = a.lastStatePayload
		}
		a.pendingResponse = nil
		a.awaitingAction.Store(false)
		_ = a.sendBot(contractMessage{V: contractVersion, Type: "turn", Payload: terminalPayload})
	default:
		if status == "err" {
			fmt.Fprintf(os.Stderr, "[agent] server err type=%s payload=%v\n", msgType, msg.Payload)
		}
	}
}

func (a *agent) sendBot(msg contractMessage) error {
	payload, err := json.Marshal(msg)
	if err != nil {
		return err
	}

	a.botMu.Lock()
	defer a.botMu.Unlock()

	if a.botConn == nil {
		return errors.New("local bot is not connected")
	}
	_, err = a.botConn.Write(append(payload, '\n'))
	return err
}

func (a *agent) sendServerCommand(command string) error {
	if a.conn == nil {
		return errors.New("server connection is not established")
	}
	line := strings.TrimSpace(command)
	if line == "" {
		return nil
	}

	a.serverMu.Lock()
	defer a.serverMu.Unlock()

	_, err := a.conn.Write([]byte(line + "\n"))
	return err
}

func (a *agent) shutdown(reason string) {
	a.cancel()

	_ = a.sendBot(contractMessage{
		V:    contractVersion,
		Type: "shutdown",
		Payload: map[string]interface{}{
			"reason": reason,
		},
	})

	_ = a.sendServerCommand("QUIT")

	if a.conn != nil {
		_ = a.conn.Close()
	}
	if a.localListener != nil {
		_ = a.localListener.Close()
	}
	if a.botConn != nil {
		_ = a.botConn.Close()
	}
	if strings.TrimSpace(a.localSocketPath) != "" {
		_ = os.Remove(a.localSocketPath)
	}
}

func parseServerAddress(raw string) (string, int, error) {
	value := strings.TrimSpace(raw)
	if value == "" {
		return "", 0, errors.New("--server is required")
	}

	host, portRaw, err := net.SplitHostPort(value)
	if err != nil {
		return "", 0, fmt.Errorf("invalid --server %q; expected host:port", raw)
	}
	if strings.TrimSpace(host) == "" || strings.TrimSpace(portRaw) == "" {
		return "", 0, fmt.Errorf("invalid --server %q; expected host:port", raw)
	}

	var port int
	if _, err := fmt.Sscanf(portRaw, "%d", &port); err != nil || port <= 0 || port > 65535 {
		return "", 0, fmt.Errorf("invalid --server port in %q", raw)
	}

	return host, port, nil
}

func buildRegisterCommand(name string, creds credentials, capabilitiesCSV, ownerToken string) string {
	parts := []string{"REGISTER", strings.TrimSpace(name)}

	if strings.TrimSpace(creds.BotID) != "" {
		parts = append(parts, strings.TrimSpace(creds.BotID))
	} else {
		parts = append(parts, "\"\"")
	}

	if strings.TrimSpace(creds.BotSecret) != "" {
		parts = append(parts, strings.TrimSpace(creds.BotSecret))
	} else {
		parts = append(parts, "\"\"")
	}

	caps := strings.TrimSpace(capabilitiesCSV)
	if caps != "" {
		parts = append(parts, caps)
	}

	ownerToken = strings.TrimSpace(ownerToken)
	if ownerToken != "" {
		parts = append(parts, "owner_token="+ownerToken)
	}

	return strings.Join(parts, " ")
}

func splitCapabilities(csv string) []string {
	parts := strings.Split(csv, ",")
	out := make([]string, 0, len(parts))
	for _, part := range parts {
		capability := strings.TrimSpace(part)
		if capability == "" {
			continue
		}
		out = append(out, capability)
	}
	return out
}

func shouldForwardTurn(statePayload map[string]interface{}, joinedPlayerID int) bool {
	if joinedPlayerID <= 0 {
		return true
	}
	if raw, ok := statePayload["your_turn"]; ok {
		return asBool(raw)
	}
	turnPlayer := asInt(statePayload["turn_player"])
	if turnPlayer > 0 {
		return turnPlayer == joinedPlayerID
	}
	return true
}

func loadCredentials(path string) (credentials, error) {
	path = strings.TrimSpace(path)
	if path == "" {
		return credentials{}, nil
	}

	data, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return credentials{}, nil
		}
		return credentials{}, err
	}

	var out credentials
	for _, line := range strings.Split(string(data), "\n") {
		entry := strings.TrimSpace(line)
		if entry == "" || strings.HasPrefix(entry, "#") {
			continue
		}
		if !strings.Contains(entry, "=") {
			continue
		}
		parts := strings.SplitN(entry, "=", 2)
		key := strings.ToLower(strings.TrimSpace(parts[0]))
		value := strings.TrimSpace(parts[1])
		switch key {
		case "bot_id":
			out.BotID = value
		case "bot_secret":
			out.BotSecret = value
		}
	}

	return out, nil
}

func saveCredentials(path string, creds credentials) error {
	if strings.TrimSpace(path) == "" {
		return errors.New("credentials path is empty")
	}

	content := strings.Builder{}
	content.WriteString("# Build-a-Bot Stadium bot credentials\n")
	content.WriteString("bot_id=" + strings.TrimSpace(creds.BotID) + "\n")
	content.WriteString("bot_secret=" + strings.TrimSpace(creds.BotSecret) + "\n")

	return os.WriteFile(path, []byte(content.String()), 0o600)
}

var sanitizeNameRE = regexp.MustCompile(`[^a-zA-Z0-9_-]+`)

func defaultCredentialsFilePath(name string) string {
	clean := sanitizeNameRE.ReplaceAllString(strings.TrimSpace(name), "_")
	clean = strings.Trim(clean, "_")
	if clean == "" {
		clean = "agent_bot"
	}
	return clean + "_credentials.txt"
}

func asString(v interface{}) string {
	switch val := v.(type) {
	case string:
		return strings.TrimSpace(val)
	default:
		if v == nil {
			return ""
		}
		return strings.TrimSpace(fmt.Sprintf("%v", v))
	}
}

func asBool(v interface{}) bool {
	switch val := v.(type) {
	case bool:
		return val
	case string:
		lower := strings.ToLower(strings.TrimSpace(val))
		return lower == "true" || lower == "1" || lower == "yes"
	default:
		return false
	}
}

func asNumber(v interface{}) interface{} {
	switch val := v.(type) {
	case float64:
		if float64(int64(val)) == val {
			return int64(val)
		}
		return val
	case int, int32, int64, uint, uint32, uint64:
		return val
	default:
		return v
	}
}

func asInt(v interface{}) int {
	switch val := v.(type) {
	case int:
		return val
	case int32:
		return int(val)
	case int64:
		return int(val)
	case float64:
		return int(val)
	case json.Number:
		i, err := val.Int64()
		if err == nil {
			return int(i)
		}
		f, ferr := val.Float64()
		if ferr == nil {
			return int(f)
		}
		return 0
	case string:
		trimmed := strings.TrimSpace(val)
		if trimmed == "" {
			return 0
		}
		var parsed int
		if _, err := fmt.Sscanf(trimmed, "%d", &parsed); err == nil {
			return parsed
		}
		return 0
	default:
		return 0
	}
}

func terminalReward(msgType string, payload interface{}, joinedPlayerID int, status string) float64 {
	if strings.EqualFold(status, "err") && (msgType == "timeout" || msgType == "ejected") {
		return -1.0
	}

	if msgType != "gameover" && msgType != "episode_end" {
		return 0.0
	}

	payloadMap, ok := payload.(map[string]interface{})
	if !ok {
		return 0.0
	}

	// Episodic environments can provide explicit shaped rewards at episode_end.
	if rawReward, ok := payloadMap["reward"]; ok {
		return asFloat64(rawReward)
	}

	if asBool(payloadMap["is_draw"]) {
		return 0.0
	}

	winnerPlayerID := asInt(payloadMap["winner_player_id"])
	if winnerPlayerID <= 0 || joinedPlayerID <= 0 {
		return 0.0
	}
	if winnerPlayerID == joinedPlayerID {
		return 1.0
	}
	return -1.0
}

func buildStatePayload(raw interface{}, joinedPlayerID int) map[string]interface{} {
	payload := map[string]interface{}{
		"source": "server_data",
	}

	rawState := ""
	var stateObj map[string]interface{}

	switch val := raw.(type) {
	case string:
		rawState = strings.TrimSpace(val)
		if rawState != "" {
			var parsed interface{}
			if err := json.Unmarshal([]byte(rawState), &parsed); err == nil {
				if obj, ok := parsed.(map[string]interface{}); ok {
					stateObj = obj
				}
			}
		}
	case map[string]interface{}:
		stateObj = val
		encoded, err := json.Marshal(val)
		if err == nil {
			rawState = string(encoded)
		}
	default:
		encoded, err := json.Marshal(raw)
		if err == nil {
			rawState = string(encoded)
		}
	}

	if rawState != "" {
		payload["raw_state"] = rawState
	} else {
		payload["raw_state"] = raw
	}

	if stateObj != nil {
		payload["state_obj"] = stateObj

		turnPlayer := asInt(stateObj["turn_player"])
		if turnPlayer == 0 {
			turnPlayer = asInt(stateObj["turn"])
		}
		if turnPlayer > 0 {
			payload["turn_player"] = turnPlayer
			if joinedPlayerID > 0 {
				payload["your_turn"] = turnPlayer == joinedPlayerID
			}
		}
	}

	return payload
}

func statePayloadDone(statePayload map[string]interface{}) bool {
	if statePayload == nil {
		return false
	}

	if raw, ok := statePayload["done"]; ok {
		return asBool(raw)
	}

	stateObj, ok := statePayload["state_obj"].(map[string]interface{})
	if !ok {
		return false
	}

	return asBool(stateObj["done"])
}

func statePayloadReward(statePayload map[string]interface{}) float64 {
	if statePayload == nil {
		return 0
	}

	if raw, ok := statePayload["reward"]; ok {
		return asFloat64(raw)
	}

	stateObj, ok := statePayload["state_obj"].(map[string]interface{})
	if !ok {
		return 0
	}

	return asFloat64(stateObj["reward"])
}

func asFloat64(v interface{}) float64 {
	switch val := v.(type) {
	case float64:
		return val
	case float32:
		return float64(val)
	case int:
		return float64(val)
	case int32:
		return float64(val)
	case int64:
		return float64(val)
	case json.Number:
		f, err := val.Float64()
		if err == nil {
			return f
		}
		return 0
	case string:
		trimmed := strings.TrimSpace(val)
		if trimmed == "" {
			return 0
		}
		var parsed float64
		if _, err := fmt.Sscanf(trimmed, "%f", &parsed); err == nil {
			return parsed
		}
		return 0
	default:
		return 0
	}
}
