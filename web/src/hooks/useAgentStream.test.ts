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
    mockFetch(async () => streamOf(frame('TokenEvent', { Text: 'one' })))
    const { result } = renderHook(() => useAgentStream())
    await act(() => result.current.send('first'))
    expect(result.current.events).toHaveLength(1)

    mockFetch(async () => {
      throw new Error('second failed')
    })
    await act(() => result.current.send('second'))

    expect(result.current.events).toEqual([])
    expect(result.current.error).toBe('second failed')

    mockFetch(async () => streamOf(frame('TokenEvent', { Text: 'three' })))
    await act(() => result.current.send('third'))

    expect(result.current.error).toBeNull()
    expect(result.current.events).toEqual([{ type: 'TokenEvent', Text: 'three' }])
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

  it('reports a malformed frame instead of swallowing it', async () => {
    mockFetch(async () => streamOf('event: TokenEvent\ndata: {oops\n\n'))
    const { result } = renderHook(() => useAgentStream())

    await act(() => result.current.send('q'))

    expect(result.current.error).toMatch(/JSON/i)
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
