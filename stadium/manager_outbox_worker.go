package stadium

import (
	"context"
	"fmt"
	"strings"
	"time"
)

// OutboxPublisher delivers durable outbox events to a remote destination.
type OutboxPublisher interface {
	PublishOutboxEvent(ctx context.Context, event DurableOutboxEvent) error
}

// NoopOutboxPublisher marks events as publishable without network delivery.
type NoopOutboxPublisher struct{}

func (NoopOutboxPublisher) PublishOutboxEvent(_ context.Context, _ DurableOutboxEvent) error {
	return nil
}

// SetOutboxPublisher sets the publisher used by the outbox worker.
func (m *Manager) SetOutboxPublisher(publisher OutboxPublisher) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.outboxPublisher = publisher
}

// StartOutboxWorker starts a polling loop that publishes pending outbox events.
func (m *Manager) StartOutboxWorker(interval time.Duration, batchSize int) {
	if interval <= 0 {
		interval = 5 * time.Second
	}
	if batchSize <= 0 {
		batchSize = 50
	}

	m.mu.Lock()
	if m.outboxStopCh != nil {
		m.mu.Unlock()
		return
	}
	stopCh := make(chan struct{})
	m.outboxStopCh = stopCh
	m.mu.Unlock()

	go func() {
		ticker := time.NewTicker(interval)
		defer ticker.Stop()
		for {
			m.processOutboxBatch(batchSize)
			select {
			case <-ticker.C:
			case <-stopCh:
				return
			}
		}
	}()
}

// StopOutboxWorker stops a running outbox worker.
func (m *Manager) StopOutboxWorker() {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.outboxStopCh == nil {
		return
	}
	close(m.outboxStopCh)
	m.outboxStopCh = nil
}

func (m *Manager) processOutboxBatch(batchSize int) {
	m.mu.Lock()
	store := m.persistence
	publisher := m.outboxPublisher
	m.mu.Unlock()

	if store == nil || publisher == nil {
		return
	}

	now := time.Now().UTC()
	events, err := store.ListPendingOutboxEvents(context.Background(), batchSize, now)
	if err != nil {
		return
	}

	for _, event := range events {
		err := publisher.PublishOutboxEvent(context.Background(), event)
		if err == nil {
			_ = store.MarkOutboxEventPublished(context.Background(), event.EventID, time.Now().UTC())
			continue
		}

		retryDelay := outboxRetryDelay(event.RetryCount)
		nextAttemptAt := time.Now().UTC().Add(retryDelay)
		_ = store.MarkOutboxEventFailed(context.Background(), event.EventID, nextAttemptAt, truncateOutboxError(err))
	}
}

func outboxRetryDelay(retryCount int) time.Duration {
	if retryCount < 0 {
		retryCount = 0
	}
	if retryCount > 6 {
		retryCount = 6
	}
	return time.Duration(1<<retryCount) * time.Second
}

func truncateOutboxError(err error) string {
	if err == nil {
		return ""
	}
	message := strings.TrimSpace(err.Error())
	if message == "" {
		message = fmt.Sprintf("%T", err)
	}
	if len(message) > 250 {
		return message[:250]
	}
	return message
}
