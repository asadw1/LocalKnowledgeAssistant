import { useCallback, useRef, useState } from 'react'
import { parseSseFrames } from '../lib/sse'

export type AgentEvent =
  | { type: 'RunStartedEvent'; RunId: string }
  | { type: 'TokenEvent'; Text: string }
  | { type: 'ToolCallEvent'; Tool: string; ArgumentsJson: string }
  | { type: 'ToolResultEvent'; Tool: string; Result: string; IsError: boolean }
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
      let buffer = ''
      let sawDone = false
      for (;;) {
        const { value, done } = await reader.read()
        if (done) break
        const parsed = parseSseFrames(buffer + value)
        buffer = parsed.rest
        if (parsed.events.some(e => e.type === 'DoneEvent')) sawDone = true
        if (parsed.events.length)
          setEvents(prev => [...prev, ...parsed.events.map(e => ({ type: e.type, ...(e.data as object) }) as AgentEvent)])
      }
      if (!sawDone) setError('The connection closed before the run finished.')
    } catch (e) {
      if (!abort.signal.aborted) setError(e instanceof Error ? e.message : String(e))
    } finally {
      setRunning(false)
    }
  }, [])

  return { events, running, error, send, stop }
}
