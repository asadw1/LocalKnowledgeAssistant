import type { ReactNode } from 'react'
import { Markdown } from './Markdown'

/** One question and its outcome, as shown in the transcript. */
export interface TurnData {
  prompt?: string
  answer: string
  /** The model's text from before each tool call; not part of the answer. */
  notes: string[]
  runId?: string
  alerts: string[]
}

/**
 * A chat bubble: the user's on the left in blue, the assistant's on the right in a neutral box.
 * Inputs: `role` and the content. Output: a labelled article; no side effects.
 * The user's text keeps its line breaks as typed; the assistant's content is passed in already rendered as markdown.
 */
export function Bubble({ role, children }: { role: 'user' | 'assistant'; children: ReactNode }) {
  const style =
    role === 'user'
      ? 'self-start whitespace-pre-wrap rounded-bl-sm bg-blue-600 text-white'
      : 'self-end rounded-br-sm border border-slate-300 bg-slate-100 dark:border-slate-700 dark:bg-slate-800'
  return (
    <article
      aria-label={role === 'user' ? 'You' : 'Assistant'}
      className={`max-w-[85%] rounded-2xl px-4 py-2 ${style}`}
    >
      {children}
    </article>
  )
}

/**
 * Renders one turn: the user's prompt bubble, collapsed working notes, the assistant's answer bubble, the run id and any error alerts.
 * Inputs: the turn and whether its run is still streaming (shows "…" until the first token).
 * A turn with no answer and no running run (for example a failed run) shows only its alerts.
 */
export function Turn({ turn, running }: { turn: TurnData; running: boolean }) {
  return (
    <>
      {turn.prompt !== undefined && <Bubble role="user">{turn.prompt}</Bubble>}
      {turn.notes.length > 0 && (
        <details className="self-end max-w-[85%] text-sm text-slate-500 dark:text-slate-400">
          <summary className="cursor-pointer">Working notes ({turn.notes.length})</summary>
          <ul className="mt-1 list-disc whitespace-pre-wrap pl-5">
            {turn.notes.map((note, i) => (
              <li key={i}>{note}</li>
            ))}
          </ul>
        </details>
      )}
      {(turn.answer || running) && <Bubble role="assistant">{turn.answer ? <Markdown>{turn.answer}</Markdown> : '…'}</Bubble>}
      {turn.runId && <p className="text-xs text-slate-500 dark:text-slate-400">Run {turn.runId}</p>}
      {turn.alerts.map((message, i) => (
        <p key={i} role="alert" className="rounded bg-red-50 p-3 text-red-700 dark:bg-red-950 dark:text-red-300">
          {message}
        </p>
      ))}
    </>
  )
}
