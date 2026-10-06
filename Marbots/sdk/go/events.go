package marbots

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
)

// EventsAPI streams live events (Server-Sent Events).
type EventsAPI struct{ c *Client }

// Stream delivers events (optionally for one thread) until ctx is cancelled or the server closes the stream.
// The error channel receives at most one error and is closed together with the event channel.
func (a *EventsAPI) Stream(ctx context.Context, threadID string) (<-chan Event, <-chan error) {
	events := make(chan Event, 64)
	errs := make(chan error, 1)
	go func() {
		defer close(events)
		defer close(errs)
		path := "/api/v1/events"
		if threadID != "" {
			path = "/api/v1/threads/" + esc(threadID) + "/events"
		}
		req, err := a.c.newRequest(ctx, http.MethodGet, path, nil, "")
		if err != nil {
			errs <- err
			return
		}
		req.Header.Set("Accept", "text/event-stream")
		// No client timeout: the stream is long-lived and bounded by ctx.
		resp, err := (&http.Client{Transport: a.c.http.Transport}).Do(req)
		if err != nil {
			if ctx.Err() == nil {
				errs <- err
			}
			return
		}
		defer resp.Body.Close()
		if resp.StatusCode >= 300 {
			errs <- &Error{Status: resp.StatusCode, Message: fmt.Sprintf("event stream unavailable (%s)", resp.Status)}
			return
		}
		sc := bufio.NewScanner(resp.Body)
		sc.Buffer(make([]byte, 64*1024), 4*1024*1024)
		for sc.Scan() {
			line := sc.Text()
			if !strings.HasPrefix(line, "data: ") {
				continue
			}
			var e Event
			if json.Unmarshal([]byte(line[6:]), &e) != nil {
				continue
			}
			select {
			case events <- e:
			case <-ctx.Done():
				return
			}
		}
		if err := sc.Err(); err != nil && ctx.Err() == nil {
			errs <- err
		}
	}()
	return events, errs
}
