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
  window.localStorage.clear()
  document.documentElement.classList.remove('dark')
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

  it('clears the input after sending, by button and by Enter', async () => {
    const user = userEvent.setup()
    render(<App />)
    const input = screen.getByPlaceholderText('Type a prompt')

    await user.type(input, 'first')
    await user.click(screen.getByRole('button', { name: 'Send' }))
    expect(input).toHaveValue('')

    await user.type(input, 'second{Enter}')
    expect(input).toHaveValue('')
    expect(hook.send.mock.calls).toEqual([['first'], ['second']])
  })

  it('shows each sent prompt as a user bubble followed by the assistant bubble, keeping earlier turns', async () => {
    const user = userEvent.setup()
    const { rerender } = render(<App />)
    const input = screen.getByPlaceholderText('Type a prompt')

    await user.type(input, 'one{Enter}')
    hook.state = { events: [{ type: 'TokenEvent', Text: 'Answer one' }, { type: 'DoneEvent' }], running: false, error: null }
    rerender(<App />)
    await user.type(input, 'two{Enter}')
    hook.state = { events: [{ type: 'TokenEvent', Text: 'Answer two' }, { type: 'DoneEvent' }], running: false, error: null }
    rerender(<App />)

    const bubbles = screen.getAllByRole('article')
    expect(bubbles.map(b => [b.getAttribute('aria-label'), b.textContent])).toEqual([
      ['You', 'one'],
      ['Assistant', 'Answer one'],
      ['You', 'two'],
      ['Assistant', 'Answer two'],
    ])
    expect(screen.queryByText('Ask something to get started.')).not.toBeInTheDocument()
  })

  it('keeps a failed turn (alert, no answer bubble) in the transcript when the next prompt is sent', async () => {
    const user = userEvent.setup()
    const { rerender } = render(<App />)
    const input = screen.getByPlaceholderText('Type a prompt')

    await user.type(input, 'one{Enter}')
    hook.state = { events: [{ type: 'ErrorEvent', Message: 'model down' }, { type: 'DoneEvent' }], running: false, error: null }
    rerender(<App />)
    await user.type(input, 'two{Enter}')
    hook.state = { events: [], running: true, error: null }
    rerender(<App />)

    expect(screen.getAllByRole('article').map(b => b.textContent)).toEqual(['one', 'two', '…'])
    expect(screen.getByRole('alert')).toHaveTextContent('model down')
  })

  it('cycles through sent prompts with ArrowUp/ArrowDown and restores the draft', async () => {
    const user = userEvent.setup()
    render(<App />)
    const input = screen.getByPlaceholderText('Type a prompt')
    await user.type(input, 'first{Enter}')
    await user.type(input, 'second{Enter}')

    await user.type(input, 'draft{ArrowUp}')
    expect(input).toHaveValue('second')
    await user.keyboard('{ArrowUp}')
    expect(input).toHaveValue('first')
    await user.keyboard('{ArrowDown}')
    expect(input).toHaveValue('second')
    await user.keyboard('{ArrowDown}')
    expect(input).toHaveValue('draft')
  })

  it('leaves the input alone for arrow keys with no history, other keys, and ArrowDown when not browsing', async () => {
    const user = userEvent.setup()
    render(<App />)
    const input = screen.getByPlaceholderText('Type a prompt')

    await user.type(input, 'abc{ArrowUp}{ArrowDown}{ArrowLeft}')

    expect(input).toHaveValue('abc')
  })

  it('returns focus to the input when a run ends', () => {
    hook.state = { events: [], running: true, error: null }
    const { rerender } = render(<App />)
    expect(screen.getByPlaceholderText('Type a prompt')).not.toHaveFocus()

    hook.state = { events: [], running: false, error: null }
    rerender(<App />)

    expect(screen.getByPlaceholderText('Type a prompt')).toHaveFocus()
  })

  it('toggles dark mode on and off from the header button', async () => {
    const user = userEvent.setup()
    render(<App />)

    await user.click(screen.getByRole('button', { name: 'Dark mode' }))
    expect(document.documentElement).toHaveClass('dark')

    await user.click(screen.getByRole('button', { name: 'Light mode' }))
    expect(document.documentElement).not.toHaveClass('dark')
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

  it('shows text from before a tool call as collapsed working notes, not as part of the answer', async () => {
    hook.state = {
      events: [
        { type: 'TokenEvent', Text: "Let's query the database." },
        { type: 'ToolCallEvent', Tool: 'sqlite-query', ArgumentsJson: '{}' },
        { type: 'ToolResultEvent', Tool: 'sqlite-query', Result: 'x', IsError: false },
        { type: 'TokenEvent', Text: 'The final answer.' },
        { type: 'DoneEvent' },
      ],
      running: false,
      error: null,
    }
    const user = userEvent.setup()
    render(<App />)

    expect(screen.getByRole('article', { name: 'Assistant' })).toHaveTextContent('The final answer.')
    expect(screen.getByRole('article', { name: 'Assistant' })).not.toHaveTextContent('query the database')
    const notes = screen.getByText('Working notes (1)')
    expect(screen.getByText("Let's query the database.")).not.toBeVisible()
    await user.click(notes)
    expect(screen.getByText("Let's query the database.")).toBeVisible()
  })

  it('shows the streamed answer made only of token text', () => {
    hook.state = {
      events: [
        { type: 'TokenEvent', Text: 'Hel' },
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

  it('shows a run error from the stream as an alert and keeps the partial answer and run id visible', () => {
    hook.state = {
      events: [
        { type: 'RunStartedEvent', RunId: 'abc123def456' },
        { type: 'TokenEvent', Text: 'Partial answer' },
        { type: 'ErrorEvent', Message: 'Could not reach the model server.' },
        { type: 'DoneEvent' },
      ],
      running: false,
      error: null,
    }
    render(<App />)

    expect(screen.getByRole('alert')).toHaveTextContent('Could not reach the model server.')
    expect(screen.getByText('Run abc123def456')).toBeInTheDocument()
    expect(screen.getByText('Partial answer')).toBeInTheDocument()
  })

  it('shows the run id under the answer once the run has started', () => {
    hook.state = { events: [{ type: 'RunStartedEvent', RunId: 'abc123def456' }, { type: 'TokenEvent', Text: 'Hi' }], running: true, error: null }
    render(<App />)

    expect(screen.getByText('Run abc123def456')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('shows every error and the transport error as separate alerts', () => {
    hook.state = {
      events: [
        { type: 'ErrorEvent', Message: 'first' },
        { type: 'ErrorEvent', Message: 'second' },
      ],
      running: false,
      error: 'The connection closed before the run finished.',
    }
    render(<App />)

    expect(screen.getAllByRole('alert').map(a => a.textContent)).toEqual([
      'first',
      'second',
      'The connection closed before the run finished.',
    ])
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
