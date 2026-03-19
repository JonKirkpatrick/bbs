package pluginapi

import "encoding/json"

const (
	ProtocolVersion = 1
)

const (
	MethodInit           = "init"
	MethodGetName        = "get_name"
	MethodGetState       = "get_state"
	MethodValidateMove   = "validate_move"
	MethodApplyMove      = "apply_move"
	MethodIsGameOver     = "is_game_over"
	MethodAdvanceEpisode = "advance_episode"
	MethodShutdown       = "shutdown"
)

type Request struct {
	ID     uint64          `json:"id"`
	Method string          `json:"method"`
	Params json.RawMessage `json:"params,omitempty"`
}

type Response struct {
	ID     uint64          `json:"id"`
	Result json.RawMessage `json:"result,omitempty"`
	Error  *RPCError       `json:"error,omitempty"`
}

type RPCError struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

type ArgSpec struct {
	Key          string `json:"key"`
	Label        string `json:"label"`
	InputType    string `json:"input_type"`
	Placeholder  string `json:"placeholder,omitempty"`
	DefaultValue string `json:"default_value,omitempty"`
	Required     bool   `json:"required,omitempty"`
	Help         string `json:"help,omitempty"`
}

type Manifest struct {
	ProtocolVersion   int       `json:"protocol_version"`
	Name              string    `json:"name"`
	DisplayName       string    `json:"display_name"`
	Executable        string    `json:"executable"`
	ViewerClientEntry string    `json:"viewer_client_entry,omitempty"`
	SupportsReplay    bool      `json:"supports_replay,omitempty"`
	SupportsMoveClock bool      `json:"supports_move_clock"`
	SupportsHandicap  bool      `json:"supports_handicap"`
	Args              []ArgSpec `json:"args,omitempty"`
}

type InitParams struct {
	Args []string `json:"args,omitempty"`
}

type InitResult struct {
	Name              string `json:"name"`
	RequiredPlayers   int    `json:"required_players"`
	SupportsMoveClock bool   `json:"supports_move_clock"`
	SupportsHandicap  bool   `json:"supports_handicap"`
	SupportsEpisodic  bool   `json:"supports_episodic"`
}

type MoveParams struct {
	PlayerID int    `json:"player_id"`
	Move     string `json:"move"`
}

type StateResult struct {
	State string `json:"state"`
}

type NameResult struct {
	Name string `json:"name"`
}

type IsGameOverResult struct {
	IsGameOver bool   `json:"is_game_over"`
	Winner     string `json:"winner"`
}

type AdvanceEpisodeResult struct {
	Continued bool                   `json:"continued"`
	Payload   map[string]interface{} `json:"payload,omitempty"`
}

type Empty struct{}
