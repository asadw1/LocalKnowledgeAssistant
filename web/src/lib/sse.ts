/** The server's stream contained something the dashboard cannot read; the message is safe to show the user. */
export class StreamFormatError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'StreamFormatError'
  }
}

/**
 * Splits buffered SSE text into complete frames, returning the unconsumed remainder.
 * Throws StreamFormatError when a complete frame's data is not valid JSON (rather than a raw SyntaxError whose text
 * would quote the payload).
 */
export function parseSseFrames(buffer: string): { events: { type: string; data: unknown }[]; rest: string } {
  const frames = buffer.split('\n\n')
  const rest = frames.pop()! // split() always yields at least one element
  const events: { type: string; data: unknown }[] = []
  for (const frame of frames) {
    const type = /^event: (.+)$/m.exec(frame)?.[1]
    const data = /^data: (.+)$/m.exec(frame)?.[1]
    if (!type || !data) continue
    try {
      events.push({ type, data: JSON.parse(data) })
    } catch {
      // The event name is not echoed: it came from the stream and is not known to be safe or short.
      throw new StreamFormatError('The server sent an event the dashboard could not read.')
    }
  }
  return { events, rest }
}
