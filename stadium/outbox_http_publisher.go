package stadium

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// HTTPOutboxPublisher publishes outbox events to a remote HTTP endpoint.
type HTTPOutboxPublisher struct {
	endpointURL string
	authToken   string
	client      *http.Client
}

// NewHTTPOutboxPublisher creates a new HTTP outbox publisher with a configured timeout.
func NewHTTPOutboxPublisher(endpointURL, authToken string, timeout time.Duration) (*HTTPOutboxPublisher, error) {
	endpointURL = strings.TrimSpace(endpointURL)
	if endpointURL == "" {
		return nil, errors.New("endpoint URL is required")
	}
	if timeout <= 0 {
		timeout = 5 * time.Second
	}
	return &HTTPOutboxPublisher{
		endpointURL: endpointURL,
		authToken:   strings.TrimSpace(authToken),
		client:      &http.Client{Timeout: timeout},
	}, nil
}

type httpOutboxRequest struct {
	EventID        string          `json:"event_id"`
	OriginServerID string          `json:"origin_server_id"`
	EventType      string          `json:"event_type"`
	Payload        json.RawMessage `json:"payload,omitempty"`
	PayloadJSON    string          `json:"payload_json,omitempty"`
	CreatedAt      string          `json:"created_at"`
}

type httpOutboxResponse struct {
	Status string `json:"status"`
	AckID  string `json:"ack_id"`
	Error  string `json:"error"`
}

func (p *HTTPOutboxPublisher) PublishOutboxEvent(ctx context.Context, event DurableOutboxEvent) error {
	payload := httpOutboxRequest{
		EventID:        event.EventID,
		OriginServerID: event.OriginServerID,
		EventType:      event.EventType,
		CreatedAt:      event.CreatedAt.UTC().Format(time.RFC3339Nano),
	}
	if json.Valid([]byte(event.PayloadJSON)) {
		payload.Payload = json.RawMessage(event.PayloadJSON)
	} else {
		payload.PayloadJSON = event.PayloadJSON
	}

	body, err := json.Marshal(payload)
	if err != nil {
		return err
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, p.endpointURL, bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	if p.authToken != "" {
		req.Header.Set("Authorization", "Bearer "+p.authToken)
	}

	resp, err := p.client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(io.LimitReader(resp.Body, 64*1024))
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		message := strings.TrimSpace(string(respBody))
		if message == "" {
			message = http.StatusText(resp.StatusCode)
		}
		return fmt.Errorf("outbox publish failed: status=%d message=%s", resp.StatusCode, message)
	}

	if len(bytes.TrimSpace(respBody)) == 0 {
		return nil
	}

	var ack httpOutboxResponse
	if err := json.Unmarshal(respBody, &ack); err != nil {
		// 2xx with non-JSON body is accepted as successful delivery.
		return nil
	}

	status := strings.ToLower(strings.TrimSpace(ack.Status))
	switch status {
	case "", "ok", "accepted":
		return nil
	case "error", "failed", "rejected":
		if strings.TrimSpace(ack.Error) != "" {
			return errors.New(strings.TrimSpace(ack.Error))
		}
		return errors.New("remote endpoint rejected outbox event")
	default:
		// Unknown status in 2xx response is treated as success to avoid false negatives.
		return nil
	}
}
