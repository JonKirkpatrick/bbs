package games

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"sync"

	"github.com/JonKirkpatrick/bbs/games/pluginapi"
)

type pluginRPCClient struct {
	mu     sync.Mutex
	cmd    *exec.Cmd
	stdin  *json.Encoder
	stdout *json.Decoder
	nextID uint64
}

func newPluginRPCClient(executable string) (*pluginRPCClient, error) {
	cmd := exec.Command(executable)
	cmd.Stderr = os.Stderr

	stdinPipe, err := cmd.StdinPipe()
	if err != nil {
		return nil, fmt.Errorf("failed to create plugin stdin pipe: %w", err)
	}

	stdoutPipe, err := cmd.StdoutPipe()
	if err != nil {
		return nil, fmt.Errorf("failed to create plugin stdout pipe: %w", err)
	}

	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("failed starting plugin process: %w", err)
	}

	return &pluginRPCClient{
		cmd:    cmd,
		stdin:  json.NewEncoder(stdinPipe),
		stdout: json.NewDecoder(stdoutPipe),
	}, nil
}

func (c *pluginRPCClient) call(method string, params interface{}, result interface{}) error {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.nextID++
	id := c.nextID

	var paramsRaw json.RawMessage
	if params != nil {
		encoded, err := json.Marshal(params)
		if err != nil {
			return fmt.Errorf("failed encoding params for %s: %w", method, err)
		}
		paramsRaw = encoded
	}

	req := pluginapi.Request{
		ID:     id,
		Method: method,
		Params: paramsRaw,
	}
	if err := c.stdin.Encode(req); err != nil {
		return fmt.Errorf("failed sending %s request: %w", method, err)
	}

	var resp pluginapi.Response
	if err := c.stdout.Decode(&resp); err != nil {
		return fmt.Errorf("failed reading %s response: %w", method, err)
	}

	if resp.ID != id {
		return fmt.Errorf("protocol error: expected response id %d, got %d", id, resp.ID)
	}
	if resp.Error != nil {
		return fmt.Errorf("%s", resp.Error.Message)
	}

	if result != nil && len(resp.Result) > 0 {
		if err := json.Unmarshal(resp.Result, result); err != nil {
			return fmt.Errorf("failed decoding %s result: %w", method, err)
		}
	}

	return nil
}

func (c *pluginRPCClient) close() error {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.cmd == nil {
		return nil
	}

	if c.cmd.Process != nil {
		_ = c.cmd.Process.Kill()
	}
	_, _ = c.cmd.Process.Wait()
	c.cmd = nil
	return nil
}

type processPluginGame struct {
	name              string
	requiredPlayers   int
	supportsMoveClock bool
	supportsHandicap  bool
	client            *pluginRPCClient
	closeOnce         sync.Once
	closeErr          error
}

type processPluginEpisodicGame struct {
	*processPluginGame
}

func launchPluginGame(name, executable string, args []string) (GameInstance, error) {
	client, err := newPluginRPCClient(executable)
	if err != nil {
		return nil, fmt.Errorf("failed to launch plugin game %q: %w", name, err)
	}

	initResult := pluginapi.InitResult{}
	if err := client.call(pluginapi.MethodInit, pluginapi.InitParams{Args: args}, &initResult); err != nil {
		_ = client.close()
		return nil, fmt.Errorf("failed initializing plugin game %q: %w", name, err)
	}

	base := &processPluginGame{
		name:              initResult.Name,
		requiredPlayers:   initResult.RequiredPlayers,
		supportsMoveClock: initResult.SupportsMoveClock,
		supportsHandicap:  initResult.SupportsHandicap,
		client:            client,
	}
	if base.name == "" {
		base.name = name
	}
	if base.requiredPlayers < 0 || base.requiredPlayers > 2 {
		base.requiredPlayers = 2
	}

	if initResult.SupportsEpisodic {
		return &processPluginEpisodicGame{processPluginGame: base}, nil
	}

	return base, nil
}

func (p *processPluginGame) GetName() string {
	return p.name
}

func (p *processPluginGame) GetState() string {
	result := pluginapi.StateResult{}
	if err := p.client.call(pluginapi.MethodGetState, pluginapi.Empty{}, &result); err != nil {
		fallback, _ := json.Marshal(map[string]string{"error": err.Error()})
		return string(fallback)
	}
	return result.State
}

func (p *processPluginGame) ValidateMove(playerID int, move string) error {
	return p.client.call(pluginapi.MethodValidateMove, pluginapi.MoveParams{PlayerID: playerID, Move: move}, &pluginapi.Empty{})
}

func (p *processPluginGame) ApplyMove(playerID int, move string) error {
	return p.client.call(pluginapi.MethodApplyMove, pluginapi.MoveParams{PlayerID: playerID, Move: move}, &pluginapi.Empty{})
}

func (p *processPluginGame) IsGameOver() (bool, string) {
	result := pluginapi.IsGameOverResult{}
	if err := p.client.call(pluginapi.MethodIsGameOver, pluginapi.Empty{}, &result); err != nil {
		return false, ""
	}
	return result.IsGameOver, result.Winner
}

func (p *processPluginGame) RequiredPlayers() int {
	if p.requiredPlayers >= 0 && p.requiredPlayers <= 2 {
		return p.requiredPlayers
	}
	return 2
}

func (p *processPluginGame) EnforceMoveClock() bool {
	return p.supportsMoveClock
}

func (p *processPluginGame) SupportsHandicap() bool {
	return p.supportsHandicap
}

func (p *processPluginGame) Close() error {
	p.closeOnce.Do(func() {
		_ = p.client.call(pluginapi.MethodShutdown, pluginapi.Empty{}, &pluginapi.Empty{})
		p.closeErr = p.client.close()
	})
	return p.closeErr
}

func (p *processPluginEpisodicGame) AdvanceEpisode() (bool, map[string]interface{}, error) {
	result := pluginapi.AdvanceEpisodeResult{}
	if err := p.client.call(pluginapi.MethodAdvanceEpisode, pluginapi.Empty{}, &result); err != nil {
		return false, nil, err
	}
	return result.Continued, result.Payload, nil
}
