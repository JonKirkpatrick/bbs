package stadium

import "encoding/json"

// Response represents the standardized format for messages sent from the stadium manager to bots, including status, type, and payload.
type Response struct {
	Status  string      `json:"status"`  // "ok", "err", "update"
	Type    string      `json:"type"`    // "move", "gameover", "state", "info"
	Payload interface{} `json:"payload"` // The actual message or data
}

func (s *Session) SendJSON(res Response) {
	s.mu.Lock() // Protect THIS session's connection
	defer s.mu.Unlock()

	data, _ := json.Marshal(res)
	s.Conn.Write(append(data, '\n'))
}
