/** Splits buffered SSE text into complete frames, returning the unconsumed remainder. */
export function parseSseFrames(buffer: string): { events: { type: string; data: unknown }[]; rest: string } {
  const frames = buffer.split('\n\n')
  const rest = frames.pop()! // split() always yields at least one element
  const events: { type: string; data: unknown }[] = []
  for (const frame of frames) {
    const type = /^event: (.+)$/m.exec(frame)?.[1]
    const data = /^data: (.+)$/m.exec(frame)?.[1]
    if (type && data) events.push({ type, data: JSON.parse(data) })
  }
  return { events, rest }
}
