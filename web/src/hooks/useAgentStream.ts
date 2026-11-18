import { useCallback, useRef, useState } from 'react'
import { toAgentEvent } from '../lib/events'
import { parseSseFrames } from '../lib/sse'

export type AgentEvent =
  | { type: 'RunStartedEvent'; RunId: string }
  | { type: 'TokenEvent'; Text: string }
  | { type: 'ToolCallEvent'; Tool: string; ArgumentsJson: string; at?: number }
  | { type: 'ToolResultEvent'; Tool: string; Result: string; IsError: boolean; at?: number }
  | { type: 'ErrorEvent'; Message: string }
  | { type: 'DoneEvent' }

/**
 * POSTs a prompt and consumes the SSE response (fetch-based, since EventSource can't POST).
 * Every completed run ends with a DoneEvent; a stream that closes without one is reported as an error.
 */
export function useAgentStream() {
  const [events, setEvents] = useState<AgentEvent[]>([])
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  const stop = useCallback(() => abortRef.current?.abort(), [])

  const send = useCallback(async (prompt: string) => {
    const abort = new AbortController()
    abortRef.current = abort
    setEvents([])
    setError(null)
    setRunning(true)
    try {
      const res = await fetch('/api/chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ prompt }),
        signal: abort.signal,
      })
      if (!res.ok || !res.body) throw new Error(`Request failed (${res.status})`)
      const reader = res.body.pipeThrough(new TextDecoderStream()).getReader()
      try {
        let buffer = ''
        let sawDone = false
        for (;;) {
          const { value, done } = await reader.read()
          if (done) break
          const parsed = parseSseFrames(buffer + value)
          buffer = parsed.rest
          // Validate every frame of the chunk before showing any of it, so a malformed frame cannot leave half a chunk on screen.
          const at = Date.now()
          const received = parsed.events.flatMap(e => {
            const event = toAgentEvent(e.type, e.data)
            if (!event) return []
            if (event.type === 'DoneEvent') sawDone = true
            // Tool events get `at`, the client's receive time (ms), for tool durations; the server sends no timestamps.
            return [event.type === 'ToolCallEvent' || event.type === 'ToolResultEvent' ? { ...event, at } : event]
          })
          if (received.length) setEvents(prev => [...prev, ...received])
        }
        if (!sawDone) setError('The connection closed before the run finished.')
      } finally {
        // Release the connection when we stop reading early (malformed data, abort); a failure to cancel changes nothing.
        try {
          await reader.cancel()
        } catch {
          /* already closed or errored */
        }
      }
    } catch (e) {
      // Stop is not an error. Everything else is shown: our own messages (HTTP status, StreamFormatError) or the browser's network error text.
      if (!abort.signal.aborted) setError(e instanceof Error ? e.message : String(e))
    } finally {
      setRunning(false)
    }
  }, [])

  return { events, running, error, send, stop }
}
