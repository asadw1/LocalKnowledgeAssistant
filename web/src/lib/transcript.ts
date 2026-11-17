import type { AgentEvent } from '../hooks/useAgentStream'

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
