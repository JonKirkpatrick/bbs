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
	"os/exec"
	"os/signal"
	"regexp"
	"strings"
	"sync"
	"syscall"
	"time"
)

const (
	contractVersion = "0.1"
	agentVersion    = "0.1.0"
)

type repeatedStringFlag []string

func (r *repeatedStringFlag) String() string {
	return strings.Join(*r, ",")
}

func (r *repeatedStringFlag) Set(value string) error {
	*r = append(*r, value)
	return nil
}

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

type workerEnvelope struct {
	V       string          `json:"v"`
	Type    string          `json:"type"`
	ID      string          `json:"id,omitempty"`
	Payload json.RawMessage `json:"payload"`
}

type agent struct {
	ctx    context.Context
	cancel context.CancelFunc

	name         string
	server       string
	ownerToken   string
	capabilities string

	workerCmd    *exec.Cmd
	workerStdin  io.WriteCloser
	workerStdout io.ReadCloser
	workerStderr io.ReadCloser
	workerMu     sync.Mutex

	conn       net.Conn
	serverMu   sync.Mutex
	helloAckCh chan struct{}

	registerCh      chan serverMessage
	serverErrCh     chan error
	workerReadErrCh chan error

	joinedPlayerID int
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

	creds, err := loadCredentials(cfg.credentialsFile)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[agent] failed to load credentials: %v\n", err)
		return 1
	}

	ag, err := newAgent(cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[agent] failed to initialize: %v\n", err)
		return 1
	}
	defer ag.shutdown("agent_exit")

	if err := ag.startWorker(); err != nil {
		fmt.Fprintf(os.Stderr, "[agent] failed to start worker: %v\n", err)
		return 1
	}

	if err := ag.sendWorker(contractMessage{
		V:    contractVersion,
		Type: "hello",
		Payload: map[string]interface{}{
			"agent_name":    "bbs-agent",
			"agent_version": agentVersion,
			"server":        cfg.server,
		},
	}); err != nil {
		fmt.Fprintf(os.Stderr, "[agent] failed to send hello to worker: %v\n", err)
		return 1
	}

	if err := waitForHelloAck(ag.helloAckCh, cfg.helloTimeout); err != nil {
		fmt.Fprintf(os.Stderr, "[agent] worker handshake failed: %v\n", err)
		return 1
	}

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

	registeredPayload := map[string]interface{}{
		"session_id":      asNumber(registerPayload["session_id"]),
		"bot_id":          asString(registerPayload["bot_id"]),
		"is_new_identity": asBool(registerPayload["is_new_identity"]),
		"auth_mode":       asString(registerPayload["authentication"]),
	}
	if err := ag.sendWorker(contractMessage{V: contractVersion, Type: "registered", Payload: registeredPayload}); err != nil {
		fmt.Fprintf(os.Stderr, "[agent] failed to forward registered payload: %v\n", err)
		return 1
	}

	fmt.Fprintln(os.Stderr, "[agent] ready: worker <-> server bridge active")

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
	case err := <-ag.workerReadErrCh:
		if err != nil && !errors.Is(err, io.EOF) {
			fmt.Fprintf(os.Stderr, "[agent] worker reader stopped: %v\n", err)
		}
	}

	return 0
}

type runtimeConfig struct {
	server          string
	name            string
	ownerToken      string
	capabilities    string
	credentialsFile string
	workerBin       string
	workerArgs      []string
	helloTimeout    time.Duration
	registerTimeout time.Duration
}

func parseFlags() (runtimeConfig, error) {
	var cfg runtimeConfig
	var workerArgs repeatedStringFlag

	flag.StringVar(&cfg.server, "server", "", "BBS server endpoint in host:port format")
	flag.StringVar(&cfg.name, "name", "agent_bot", "bot display name used during REGISTER")
	flag.StringVar(&cfg.ownerToken, "owner-token", "", "optional owner token from dashboard")
	flag.StringVar(&cfg.capabilities, "capabilities", "connect4", "comma-separated capability list")
	flag.StringVar(&cfg.credentialsFile, "credentials-file", "", "path to bot credentials file (key=value format)")
	flag.StringVar(&cfg.workerBin, "worker", "", "worker executable path (required)")
	flag.Var(&workerArgs, "worker-arg", "argument to pass to worker process (repeat flag for multiple args)")
	flag.DurationVar(&cfg.helloTimeout, "hello-timeout", 8*time.Second, "worker hello_ack timeout")
	flag.DurationVar(&cfg.registerTimeout, "register-timeout", 12*time.Second, "server register response timeout")

	flag.Parse()

	if _, _, err := parseServerAddress(cfg.server); err != nil {
		return cfg, err
	}
	if strings.TrimSpace(cfg.workerBin) == "" {
		return cfg, errors.New("--worker is required")
	}
	if strings.Contains(cfg.name, " ") {
		return cfg, errors.New("--name cannot contain spaces")
	}
	if cfg.credentialsFile == "" {
		cfg.credentialsFile = defaultCredentialsFilePath(cfg.name)
	}
	cfg.credentialsFile = strings.TrimSpace(cfg.credentialsFile)
	cfg.workerArgs = append([]string(nil), workerArgs...)

	return cfg, nil
}

func newAgent(cfg runtimeConfig) (*agent, error) {
	ctx, cancel := context.WithCancel(context.Background())

	cmd := exec.CommandContext(ctx, cfg.workerBin, cfg.workerArgs...)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		cancel()
		return nil, err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		cancel()
		return nil, err
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		cancel()
		return nil, err
	}

	return &agent{
		ctx:             ctx,
		cancel:          cancel,
		name:            cfg.name,
		server:          cfg.server,
		ownerToken:      strings.TrimSpace(cfg.ownerToken),
		capabilities:    strings.TrimSpace(cfg.capabilities),
		workerCmd:       cmd,
		workerStdin:     stdin,
		workerStdout:    stdout,
		workerStderr:    stderr,
		helloAckCh:      make(chan struct{}, 1),
		registerCh:      make(chan serverMessage, 1),
		serverErrCh:     make(chan error, 1),
		workerReadErrCh: make(chan error, 1),
	}, nil
}

func (a *agent) startWorker() error {
	if err := a.workerCmd.Start(); err != nil {
		return err
	}

	go a.readWorkerStdout()
	go a.streamWorkerStderr()

	go func() {
		err := a.workerCmd.Wait()
		select {
		case a.workerReadErrCh <- err:
		default:
		}
	}()

	return nil
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

func waitForHelloAck(ch <-chan struct{}, timeout time.Duration) error {
	select {
	case <-ch:
		return nil
	case <-time.After(timeout):
		return fmt.Errorf("timeout waiting for hello_ack after %s", timeout)
	}
}

func waitForRegister(ch <-chan serverMessage, timeout time.Duration) (serverMessage, error) {
	select {
	case msg := <-ch:
		return msg, nil
	case <-time.After(timeout):
		return serverMessage{}, fmt.Errorf("timeout waiting for register response after %s", timeout)
	}
}

func (a *agent) readWorkerStdout() {
	scanner := bufio.NewScanner(a.workerStdout)
	buf := make([]byte, 0, 64*1024)
	scanner.Buffer(buf, 1024*1024)

	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		a.handleWorkerLine(line)
	}

	err := scanner.Err()
	select {
	case a.workerReadErrCh <- err:
	default:
	}
}

func (a *agent) streamWorkerStderr() {
	scanner := bufio.NewScanner(a.workerStderr)
	for scanner.Scan() {
		fmt.Fprintf(os.Stderr, "[worker] %s\n", scanner.Text())
	}
}

func (a *agent) handleWorkerLine(line string) {
	var env workerEnvelope
	if err := json.Unmarshal([]byte(line), &env); err != nil {
		fmt.Fprintf(os.Stderr, "[agent] invalid worker JSON: %s\n", line)
		return
	}

	if env.V != contractVersion {
		fmt.Fprintf(os.Stderr, "[agent] ignoring worker message with unsupported version: %s\n", env.V)
		return
	}

	typeName := strings.ToLower(strings.TrimSpace(env.Type))
	switch typeName {
	case "hello_ack":
		select {
		case a.helloAckCh <- struct{}{}:
		default:
		}
	case "move":
		var payload struct {
			Move string `json:"move"`
		}
		if err := json.Unmarshal(env.Payload, &payload); err != nil {
			fmt.Fprintf(os.Stderr, "[agent] worker move payload parse error: %v\n", err)
			return
		}
		move := strings.TrimSpace(payload.Move)
		if move == "" {
			return
		}
		_ = a.sendServerCommand("MOVE " + move)
	case "command":
		var payload struct {
			Text string `json:"text"`
		}
		if err := json.Unmarshal(env.Payload, &payload); err != nil {
			fmt.Fprintf(os.Stderr, "[agent] worker command payload parse error: %v\n", err)
			return
		}
		cmdText := strings.TrimSpace(payload.Text)
		if cmdText == "" {
			return
		}
		_ = a.sendServerCommand(cmdText)
	case "set_profile":
		var payload struct {
			Name         string   `json:"name"`
			Capabilities []string `json:"capabilities"`
		}
		if err := json.Unmarshal(env.Payload, &payload); err != nil {
			fmt.Fprintf(os.Stderr, "[agent] worker set_profile payload parse error: %v\n", err)
			return
		}
		if strings.TrimSpace(payload.Name) != "" {
			_ = a.sendServerCommand("UPDATE name " + strings.TrimSpace(payload.Name))
		}
		for _, capability := range payload.Capabilities {
			capability = strings.TrimSpace(capability)
			if capability == "" {
				continue
			}
			_ = a.sendServerCommand("UPDATE capability " + capability)
		}
	case "log":
		var payload struct {
			Level   string `json:"level"`
			Message string `json:"message"`
		}
		if err := json.Unmarshal(env.Payload, &payload); err != nil {
			fmt.Fprintf(os.Stderr, "[agent] worker log payload parse error: %v\n", err)
			return
		}
		fmt.Fprintf(os.Stderr, "[worker:%s] %s\n", strings.TrimSpace(payload.Level), strings.TrimSpace(payload.Message))
	default:
		fmt.Fprintf(os.Stderr, "[agent] ignoring unsupported worker message type=%s\n", env.Type)
	}
}

func (a *agent) readServer() {
	scanner := bufio.NewScanner(a.conn)
	buf := make([]byte, 0, 64*1024)
	scanner.Buffer(buf, 1024*1024)

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
		_ = a.sendWorker(contractMessage{
			V:    contractVersion,
			Type: "event",
			Payload: map[string]interface{}{
				"name": "server_text",
				"data": map[string]interface{}{"line": line},
			},
		})
		return
	}

	msgType := strings.ToLower(strings.TrimSpace(msg.Type))
	status := strings.ToLower(strings.TrimSpace(msg.Status))

	if msgType == "register" {
		select {
		case a.registerCh <- msg:
		default:
		}
	}

	switch msgType {
	case "join":
		if payloadMap, ok := msg.Payload.(map[string]interface{}); ok {
			a.joinedPlayerID = asInt(payloadMap["player_id"])
			_ = a.sendWorker(contractMessage{V: contractVersion, Type: "manifest", Payload: payloadMap})
		}
	case "data":
		statePayload := buildStatePayload(msg.Payload, a.joinedPlayerID)
		_ = a.sendWorker(contractMessage{V: contractVersion, Type: "state", Payload: statePayload})
	default:
		eventPayload := map[string]interface{}{
			"name": msgType,
			"data": map[string]interface{}{
				"status":  status,
				"payload": msg.Payload,
			},
		}
		_ = a.sendWorker(contractMessage{V: contractVersion, Type: "event", Payload: eventPayload})
	}

	if status == "err" {
		_ = a.sendWorker(contractMessage{
			V:    contractVersion,
			Type: "error",
			Payload: map[string]interface{}{
				"code":      msgType,
				"message":   fmt.Sprintf("%v", msg.Payload),
				"retryable": false,
			},
		})
	}
}

func (a *agent) sendWorker(msg contractMessage) error {
	payload, err := json.Marshal(msg)
	if err != nil {
		return err
	}

	a.workerMu.Lock()
	defer a.workerMu.Unlock()

	_, err = a.workerStdin.Write(append(payload, '\n'))
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

	_ = a.sendWorker(contractMessage{
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
	if a.workerStdin != nil {
		_ = a.workerStdin.Close()
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
