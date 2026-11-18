import type { AgentEvent } from '../hooks/useAgentStream'

/** One tool invocation, paired with its result once that arrives. */
export interface ToolCallData {
  tool: string
  argumentsJson: string
  status: 'running' | 'ok' | 'error'
  result?: string
  /** Client-side: from receiving the call to receiving the result; includes stream latency. Undefined while running. */
  durationMs?: number
}

/**
 * Pairs each ToolCallEvent with its ToolResultEvent, in order.
 * A result belongs to the earliest still-open call with the same tool name (the runner executes calls in order);
 * a result with no open call is ignored. Calls without a result yet are 'running'.
 * Pure function: no side effects.
 */
export function buildToolCalls(events: AgentEvent[]): ToolCallData[] {
  const calls: (ToolCallData & { startedAt?: number })[] = []
  for (const e of events) {
    if (e.type === 'ToolCallEvent') {
      calls.push({ tool: e.Tool, argumentsJson: e.ArgumentsJson, status: 'running', startedAt: e.at })
    } else if (e.type === 'ToolResultEvent') {
      const open = calls.find(c => c.status === 'running' && c.tool === e.Tool)
      if (!open) continue
      open.status = e.IsError ? 'error' : 'ok'
      open.result = e.Result
      if (open.startedAt !== undefined && e.at !== undefined) open.durationMs = Math.max(0, e.at - open.startedAt)
    }
  }
  return calls.map(({ startedAt: _startedAt, ...call }) => call)
}

/**
 * Splits a run's tokens into the final answer and the model's working notes.
 *
 * The runner streams the text of every loop iteration. Text written before a tool call ("Let's query the
 * database...") is narration, not the answer; only the text after the last tool call is. Each tool call
 * closes the current segment as a note (blank segments are dropped). While a run is still streaming, the
 * tokens so far are shown as the answer and move to the notes if a tool call follows.
 * Pure function: no side effects.
 */
export function splitAnswer(events: AgentEvent[]): { answer: string; notes: string[] } {
  const notes: string[] = []
  let current = ''
  for (const e of events) {
    if (e.type === 'TokenEvent') {
      current += e.Text
    } else if (e.type === 'ToolCallEvent') {
      if (current.trim()) notes.push(current.trim())
      current = ''
    }
  }
  return { answer: current, notes }
}
