import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import { useAgentStream, type AgentEvent } from './hooks/useAgentStream'

const frame = (type: string, data: object) => `event: ${type}\ndata: ${JSON.stringify(data)}\n\n`

function streamResponse(...chunks: string[]) {
  const encoder = new TextEncoder()
  return new Response(
    new ReadableStream({
      start(controller) {
        for (const chunk of chunks) controller.enqueue(encoder.encode(chunk))
        controller.close()
      },
    }),
  )
}

// The hook is replaced for the state-driven tests; the end-to-end test below swaps the real one back in.
const hook = vi.hoisted(() => ({
  state: { events: [] as AgentEvent[], running: false, error: null as string | null },
  send: vi.fn(),
  stop: vi.fn(),
}))

vi.mock('./hooks/useAgentStream', async importOriginal => {
  const actual = await importOriginal<typeof import('./hooks/useAgentStream')>()
  return {
    ...actual,
    useAgentStream: vi.fn(() => ({ ...hook.state, send: hook.send, stop: hook.stop })),
  }
})

beforeEach(() => {
  hook.state = { events: [], running: false, error: null }
  hook.send.mockReset()
  hook.stop.mockReset()
})

describe('App', () => {
  it('shows the title, an empty-state hint and a disabled Send button', () => {
    render(<App />)

    expect(screen.getByRole('heading', { name: 'Local Knowledge Assistant' })).toBeInTheDocument()
    expect(screen.getByText('Ask something to get started.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('enables Send once there is text and sends the trimmed prompt', async () => {
    const user = userEvent.setup()
    render(<App />)

    await user.type(screen.getByPlaceholderText('Type a prompt'), '  what files are here?  ')
    await user.click(screen.getByRole('button', { name: 'Send' }))

    expect(hook.send).toHaveBeenCalledExactlyOnceWith('what files are here?')
  })

  it('does not send a blank prompt even if the form is submitted directly', () => {
    const { container } = render(<App />)

    fireEvent.change(screen.getByPlaceholderText('Type a prompt'), { target: { value: '   ' } })
    fireEvent.submit(container.querySelector('form')!)

    expect(hook.send).not.toHaveBeenCalled()
  })

  it('does not send a second prompt while a run is in progress', () => {
    hook.state = { events: [], running: true, error: null }
    const { container } = render(<App />)

    fireEvent.submit(container.querySelector('form')!)

    expect(hook.send).not.toHaveBeenCalled()
  })

  it('while running, shows a progress marker, locks the input and offers Stop instead of Send', async () => {
    hook.state = { events: [], running: true, error: null }
    const user = userEvent.setup()
    render(<App />)

    expect(screen.getByText('…')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('Type a prompt')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Send' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Stop' }))
    expect(hook.stop).toHaveBeenCalledOnce()
  })

  it('shows the streamed answer made only of token text', () => {
    hook.state = {
      events: [
        { type: 'TokenEvent', Text: 'Hel' },
        { type: 'ToolCallEvent', Tool: 'sqlite-query', ArgumentsJson: '{}' },
        { type: 'ToolResultEvent', Tool: 'sqlite-query', Result: 'x', IsError: false },
        { type: 'TokenEvent', Text: 'lo' },
        { type: 'DoneEvent' },
      ],
      running: true,
      error: null,
    }
    render(<App />)

    expect(screen.getByText('Hello')).toBeInTheDocument()
    expect(screen.queryByText('…')).not.toBeInTheDocument()
    expect(screen.queryByText('Ask something to get started.')).not.toBeInTheDocument()
  })

  it('shows an error as an alert', () => {
    hook.state = { events: [], running: false, error: 'Request failed (500)' }
    render(<App />)

    expect(screen.getByRole('alert')).toHaveTextContent('Request failed (500)')
  })
})

describe('App with the real stream hook', () => {
  beforeEach(async () => {
    const actual = await vi.importActual<typeof import('./hooks/useAgentStream')>('./hooks/useAgentStream')
    vi.mocked(useAgentStream).mockImplementation(actual.useAgentStream)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('streams an answer into the page after Send', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        streamResponse(frame('TokenEvent', { Text: 'There are ' }), frame('TokenEvent', { Text: '3 files.' }), frame('DoneEvent', {})),
      ),
    )
    const user = userEvent.setup()
    render(<App />)

    await user.type(screen.getByPlaceholderText('Type a prompt'), 'how many files?')
    await user.click(screen.getByRole('button', { name: 'Send' }))

    expect(await screen.findByText('There are 3 files.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument()
  })
})
