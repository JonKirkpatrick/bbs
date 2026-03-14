package stadium

import "encoding/json"

type Response struct {
	Status  string `json:"status"`  // "ok", "err", "update"
	Type    string `json:"type"`    // "move", "gameover", "state", "info"
	Payload string `json:"payload"` // The actual message or data
}

// Helper to make sending easier
func (s *Session) SendJSON(res Response) {
	data, _ := json.Marshal(res)
	s.Conn.Write(append(data, '\n'))
}
