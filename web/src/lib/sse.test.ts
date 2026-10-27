import { describe, expect, it } from 'vitest'
import { parseSseFrames } from './sse'

describe('parseSseFrames', () => {
  it('returns nothing for an empty buffer', () => {
    expect(parseSseFrames('')).toEqual({ events: [], rest: '' })
  })

  it('parses every complete frame and keeps the unfinished tail', () => {
    const buffer =
      'event: TokenEvent\ndata: {"Text":"Hel"}\n\n' +
      'event: TokenEvent\ndata: {"Text":"lo"}\n\n' +
      'event: DoneEv'

    expect(parseSseFrames(buffer)).toEqual({
      events: [
        { type: 'TokenEvent', data: { Text: 'Hel' } },
        { type: 'TokenEvent', data: { Text: 'lo' } },
      ],
      rest: 'event: DoneEv',
    })
  })

  it('treats a frame without a trailing blank line as not yet complete', () => {
    const buffer = 'event: TokenEvent\ndata: {"Text":"x"}'

    expect(parseSseFrames(buffer)).toEqual({ events: [], rest: buffer })
  })

  it('completes a frame once the rest is joined with the next chunk', () => {
    const first = parseSseFrames('event: TokenEvent\ndata: {"Te')
    const second = parseSseFrames(first.rest + 'xt":"ok"}\n\n')

    expect(second.events).toEqual([{ type: 'TokenEvent', data: { Text: 'ok' } }])
    expect(second.rest).toBe('')
  })

  it.each([
    ['no event line', 'data: {"a":1}\n\n'],
    ['no data line', 'event: DoneEvent\n\n'],
    ['an unrelated comment line', ': keep-alive\n\n'],
  ])('skips a frame with %s', (_name, buffer) => {
    expect(parseSseFrames(buffer)).toEqual({ events: [], rest: '' })
  })

  it('throws on malformed JSON so the caller can surface the failure', () => {
    expect(() => parseSseFrames('event: TokenEvent\ndata: {oops\n\n')).toThrow(SyntaxError)
  })
})
