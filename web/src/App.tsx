import { useState, type FormEvent } from 'react'
import { useAgentStream } from './hooks/useAgentStream'

export default function App() {
  const [prompt, setPrompt] = useState('')
  const { events, running, error, send, stop } = useAgentStream()

  const answer = events.map(e => (e.type === 'TokenEvent' ? e.Text : '')).join('')
  const runId = events.flatMap(e => (e.type === 'RunStartedEvent' ? [e.RunId] : []))[0]
  // Run errors arrive in the stream (ErrorEvent); `error` covers transport failures. The run id lets a report be matched to the server log.
  const alerts = [...events.flatMap(e => (e.type === 'ErrorEvent' ? [e.Message] : [])), ...(error ? [error] : [])]

  function submit(e: FormEvent) {
    e.preventDefault()
    if (!prompt.trim() || running) return
    void send(prompt.trim())
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-3xl flex-col gap-4 p-6">
      <h1 className="text-2xl font-semibold">Local Knowledge Assistant</h1>

      <section className="min-h-48 flex-1 whitespace-pre-wrap rounded-lg border border-slate-300 p-4">
        {answer || (running ? '…' : <span className="text-slate-400">Ask something to get started.</span>)}
      </section>

      {runId && <p className="text-xs text-slate-500">Run {runId}</p>}

      {alerts.map((message, i) => (
        <p key={i} role="alert" className="rounded bg-red-50 p-3 text-red-700">{message}</p>
      ))}

      <form onSubmit={submit} className="flex gap-2">
        <input
          value={prompt}
          onChange={e => setPrompt(e.target.value)}
          disabled={running}
          placeholder="Type a prompt"
          className="flex-1 rounded-lg border border-slate-300 px-3 py-2 disabled:opacity-50"
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
