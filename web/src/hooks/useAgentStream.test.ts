import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useAgentStream } from './useAgentStream'

const frame = (type: string, data: object) => `event: ${type}\ndata: ${JSON.stringify(data)}\n\n`

/** A streaming response whose chunks are delivered exactly as given (so frames can be split mid-way). */
function streamOf(...chunks: string[]) {
  const encoder = new TextEncoder()
  return new Response(
    new ReadableStream({
      start(controller) {
        for (const chunk of chunks) controller.enqueue(encoder.encode(chunk))
        controller.close()
      },
    }),
    { status: 200 },
  )
}

const mockFetch = (impl: (input: string, init: RequestInit) => Promise<unknown>) =>
  vi.stubGlobal('fetch', vi.fn(impl))

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('useAgentStream', () => {
  it('starts idle', () => {
    const { result } = renderHook(() => useAgentStream())

    expect(result.current).toMatchObject({ events: [], running: false, error: null })
  })

  it('posts the prompt as JSON and collects typed events from the stream', async () => {
    const fetchMock = vi.fn(async () => streamOf(frame('TokenEvent', { Text: 'Hi' }), frame('DoneEvent', {})))
    vi.stubGlobal('fetch', fetchMock)
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('hello'))

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/chat',
      expect.objectContaining({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ prompt: 'hello' }),
      }),
    )
    expect(result.current.events).toEqual([{ type: 'TokenEvent', Text: 'Hi' }, { type: 'DoneEvent' }])
    expect(result.current.running).toBe(false)
    expect(result.current.error).toBeNull()
  })

  it('stamps tool events with the client receive time and leaves other events alone', async () => {
    vi.spyOn(Date, 'now').mockReturnValue(1234)
    mockFetch(async () =>
      streamOf(
        frame('ToolCallEvent', { Tool: 't', ArgumentsJson: '{}' }),
        frame('ToolResultEvent', { Tool: 't', Result: 'r', IsError: false }),
        frame('DoneEvent', {}),
      ),
    )
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.events).toEqual([
      { type: 'ToolCallEvent', Tool: 't', ArgumentsJson: '{}', at: 1234 },
      { type: 'ToolResultEvent', Tool: 't', Result: 'r', IsError: false, at: 1234 },
      { type: 'DoneEvent' },
    ])
  })

  it('collects the run id event like any other typed event', async () => {
    mockFetch(async () => streamOf(frame('RunStartedEvent', { RunId: 'abc123def456' }), frame('DoneEvent', {})))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.events[0]).toEqual({ type: 'RunStartedEvent', RunId: 'abc123def456' })
  })

  it('flags a stream that ends without a DoneEvent', async () => {
    mockFetch(async () => streamOf(frame('TokenEvent', { Text: 'partial' })))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toBe('The connection closed before the run finished.')
    expect(result.current.events).toEqual([{ type: 'TokenEvent', Text: 'partial' }])
    expect(result.current.running).toBe(false)
  })

  it('does not flag a stream that ends with an ErrorEvent and a DoneEvent', async () => {
    mockFetch(async () => streamOf(frame('ErrorEvent', { Message: 'boom' }), frame('DoneEvent', {})))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toBeNull()
  })

  it('reassembles a frame that arrives split across chunks', async () => {
    mockFetch(async () => streamOf('event: TokenEvent\nda', 'ta: {"Text":', '"split"}\n\n'))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.events).toEqual([{ type: 'TokenEvent', Text: 'split' }])
  })

  it('reports running while the request is in flight', async () => {
    let release!: () => void
    const gate = new Promise<void>(resolve => (release = resolve))
    mockFetch(async () => {
      await gate
      return streamOf(frame('DoneEvent', {}))
    })
    const { result } = renderHook(() => useAgentStream())

    let pending!: Promise<void>
    act(() => {
      pending = result.current.send('q')
    })
    expect(result.current.running).toBe(true)

    release()
    await act(() => pending)
    expect(result.current.running).toBe(false)
  })

  it('clears the previous run before starting a new one', async () => {
    mockFetch(async () => streamOf(frame('TokenEvent', { Text: 'one' }), frame('DoneEvent', {})))
    const { result } = renderHook(() => useAgentStream())
    await act(() => result.current.send('first'))
    expect(result.current.events).toHaveLength(2)

    mockFetch(async () => {
      throw new Error('second failed')
    })
    await act(() => result.current.send('second'))

    expect(result.current.events).toEqual([])
    expect(result.current.error).toBe('second failed')

    mockFetch(async () => streamOf(frame('TokenEvent', { Text: 'three' }), frame('DoneEvent', {})))
    await act(() => result.current.send('third'))

    expect(result.current.error).toBeNull()
    expect(result.current.events).toEqual([{ type: 'TokenEvent', Text: 'three' }, { type: 'DoneEvent' }])
  })

  it('reports a failed HTTP status', async () => {
    mockFetch(async () => new Response('nope', { status: 500 }))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toBe('Request failed (500)')
    expect(result.current.running).toBe(false)
  })

  it('reports a successful response that has no body', async () => {
    mockFetch(async () => ({ ok: true, status: 200, body: null }))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toBe('Request failed (200)')
  })

  it('reports a network error by its message', async () => {
    mockFetch(async () => {
      throw new TypeError('Failed to fetch')
    })
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toBe('Failed to fetch')
  })

  it('stringifies a non-Error rejection', async () => {
    mockFetch(() => Promise.reject('plain string'))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toBe('plain string')
  })

  it('shows a plain message for a malformed frame and keeps nothing from that chunk', async () => {
    mockFetch(async () => streamOf(frame('TokenEvent', { Text: 'ok' }) + 'event: TokenEvent\ndata: {broken\n\n'))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toBe('The server sent an event the dashboard could not read.')
    expect(result.current.events).toEqual([])
    expect(result.current.running).toBe(false)
  })

  it('reports a known event with a missing or mistyped field instead of rendering it', async () => {
    mockFetch(async () => streamOf(frame('ToolResultEvent', { Tool: 't', Result: 'r' })))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toBe('The server sent a malformed ToolResultEvent.')
    expect(result.current.events).toEqual([])
  })

  it('ignores an event type it does not know', async () => {
    mockFetch(async () => streamOf(frame('FutureEvent', { x: 1 }), frame('TokenEvent', { Text: 'hi' }), frame('DoneEvent', {})))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toBeNull()
    expect(result.current.events).toEqual([{ type: 'TokenEvent', Text: 'hi' }, { type: 'DoneEvent' }])
  })

  it('releases the connection after bad data and tolerates a stream that refuses to cancel', async () => {
    const cancelled = vi.fn()
    mockFetch(
      async () =>
        new Response(
          new ReadableStream({
            start(controller) {
              controller.enqueue(new TextEncoder().encode('event: TokenEvent\ndata: {broken\n\n'))
            },
            cancel() {
              cancelled()
              throw new Error('refuses to cancel')
            },
          }),
          { status: 200 },
        ),
    )
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(cancelled).toHaveBeenCalled()
    expect(result.current.error).toBe('The server sent an event the dashboard could not read.')
    expect(result.current.running).toBe(false)
  })

  it('stop aborts the request without reporting it as an error', async () => {
    let signal!: AbortSignal
    mockFetch(
      (_url, init) =>
        new Promise((_resolve, reject) => {
          signal = init.signal as AbortSignal
          signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')))
        }),
    )
    const { result } = renderHook(() => useAgentStream())

    let pending!: Promise<void>
    act(() => {
      pending = result.current.send('q')
    })
    await waitFor(() => expect(signal).toBeDefined())
    act(() => result.current.stop())
    await act(() => pending)

    expect(signal.aborted).toBe(true)
    expect(result.current.error).toBeNull()
    expect(result.current.running).toBe(false)
  })

  it('stop with nothing running is a no-op', () => {
    const { result } = renderHook(() => useAgentStream())

    expect(() => act(() => result.current.stop())).not.toThrow()
  })
})
