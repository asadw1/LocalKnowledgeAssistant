import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Turn, type TurnData } from './components/Turn'
import { useAgentStream } from './hooks/useAgentStream'
import { splitAnswer } from './lib/transcript'
import { usePromptHistory } from './hooks/usePromptHistory'
import { useTheme } from './hooks/useTheme'

export default function App() {
  const [prompt, setPrompt] = useState('')
  const { events, running, error, send, stop } = useAgentStream()
  const history = usePromptHistory()
  const { theme, toggle } = useTheme()
  const inputRef = useRef<HTMLInputElement>(null)

  // Finished turns. The hook only holds the latest run, so the previous one is archived here when a new prompt is sent.
  const [turns, setTurns] = useState<TurnData[]>([])
  const [currentPrompt, setCurrentPrompt] = useState<string>()

  const { answer, notes } = splitAnswer(events)
  const runId = events.flatMap(e => (e.type === 'RunStartedEvent' ? [e.RunId] : []))[0]
  // Run errors arrive in the stream (ErrorEvent); `error` covers transport failures. The run id lets a report be matched to the server log.
  const alerts = [...events.flatMap(e => (e.type === 'ErrorEvent' ? [e.Message] : [])), ...(error ? [error] : [])]
  const current: TurnData = { prompt: currentPrompt, answer, notes, runId, alerts }
  const hasCurrent = currentPrompt !== undefined || running || events.length > 0 || error !== null

  // A disabled input loses focus, so hand it back when a run ends and the user can type again.
  useEffect(() => {
    if (!running) inputRef.current?.focus()
  }, [running])

  function submit(e: FormEvent) {
    e.preventDefault()
    const text = prompt.trim()
    if (!text || running) return
    history.record(text)
    if (hasCurrent) setTurns(prev => [...prev, current])
    setCurrentPrompt(text)
    setPrompt('')
    void send(text)
  }

  function recall(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return
    const text = e.key === 'ArrowUp' ? history.previous(prompt) : history.next()
    if (text === undefined) return
    // Stop the browser from also moving the caret to the start/end of the line.
    e.preventDefault()
    setPrompt(text)
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-3xl flex-col gap-4 p-6">
      <header className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">Local Knowledge Assistant</h1>
        <button
          type="button"
          onClick={toggle}
          className="rounded-lg border border-slate-300 px-3 py-1 text-sm dark:border-slate-600"
        >
          {theme === 'dark' ? 'Light mode' : 'Dark mode'}
        </button>
      </header>

      <section aria-label="Conversation" className="flex min-h-48 flex-1 flex-col gap-3 rounded-lg border border-slate-300 p-4 dark:border-slate-700">
        {turns.map((turn, i) => (
          <Turn key={i} turn={turn} running={false} />
        ))}
        {hasCurrent ? (
          <Turn turn={current} running={running} />
        ) : (
          <span className="text-slate-400">Ask something to get started.</span>
        )}
      </section>

      <form onSubmit={submit} className="flex gap-2">
        <input
          ref={inputRef}
          value={prompt}
          onChange={e => setPrompt(e.target.value)}
          onKeyDown={recall}
          disabled={running}
          placeholder="Type a prompt"
          className="flex-1 rounded-lg border border-slate-300 bg-white px-3 py-2 disabled:opacity-50 dark:border-slate-600 dark:bg-slate-800"
        />
        {running ? (
          <button type="button" onClick={stop} className="rounded-lg bg-slate-700 px-4 py-2 text-white">Stop</button>
        ) : (
          <button type="submit" className="rounded-lg bg-blue-600 px-4 py-2 text-white disabled:opacity-50" disabled={!prompt.trim()}>Send</button>
        )}
      </form>
    </main>
  )
}
