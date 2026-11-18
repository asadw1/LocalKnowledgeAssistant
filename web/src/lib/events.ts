import type { AgentEvent } from '../hooks/useAgentStream'
import { StreamFormatError } from './sse'

const isString = (v: unknown): v is string => typeof v === 'string'

/** Required fields per known event type, with the check each value must pass. */
const SHAPES: Record<string, Record<string, (v: unknown) => boolean>> = {
  RunStartedEvent: { RunId: isString },
  TokenEvent: { Text: isString },
  ToolCallEvent: { Tool: isString, ArgumentsJson: isString },
  ToolResultEvent: { Tool: isString, Result: isString, IsError: v => typeof v === 'boolean' },
  ErrorEvent: { Message: isString },
  DoneEvent: {},
}

/**
 * Turns one parsed SSE frame into a typed event, checking that its fields have the types the UI relies on.
 * Inputs: the frame's event name and parsed JSON data. Output: the event, or null for an event type this
 * dashboard does not know (ignored so a newer server does not break an older UI).
 * Throws StreamFormatError for a known type whose data is missing a field or has the wrong type, because rendering
 * it would otherwise show "undefined" or crash a component. No other side effects.
 */
export function toAgentEvent(type: string, data: unknown): AgentEvent | null {
  const shape = Object.hasOwn(SHAPES, type) ? SHAPES[type] : undefined
  if (!shape) return null
  if (typeof data !== 'object' || data === null) throw new StreamFormatError(`The server sent a malformed ${type}.`)
  const record = data as Record<string, unknown>
  for (const [field, valid] of Object.entries(shape)) {
    if (!valid(record[field])) throw new StreamFormatError(`The server sent a malformed ${type}.`)
  }
  return { type, ...record } as AgentEvent
}
