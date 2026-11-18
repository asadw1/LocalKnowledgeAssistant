import type { ToolCallData } from '../lib/transcript'

/** Formats a duration for display: "850 ms" under a second, otherwise seconds with one decimal ("1.2 s"). */
function formatDuration(ms: number): string {
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`
}

/** Pretty-prints a JSON string; text that is not valid JSON is returned unchanged. */
function prettyJson(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    return text
  }
}

/**
 * A collapsible card for one tool call: name, status, duration, then arguments and result when expanded.
 * Inputs: the paired call/result. Output: a <details> element, collapsed by default; no side effects.
 * Errors are marked in red and the result is shown as-is (it is already written for the model, so it is readable).
 * The duration is measured client-side and includes stream latency.
 */
export function ToolCard({ call }: { call: ToolCallData }) {
  const statusText = call.status === 'running' ? 'running…' : call.status === 'error' ? 'error' : 'done'
  const statusStyle =
    call.status === 'error' ? 'text-red-700 dark:text-red-300' : 'text-slate-500 dark:text-slate-400'
  return (
    <details
      aria-label={`Tool call ${call.tool}`}
      className={`w-full max-w-[85%] self-end rounded-lg border px-3 py-1 text-sm ${
        call.status === 'error' ? 'border-red-300 dark:border-red-800' : 'border-slate-300 dark:border-slate-700'
      }`}
    >
      <summary className="cursor-pointer">
        <span className="font-mono">{call.tool}</span>{' '}
        <span className={statusStyle}>
          {statusText}
          {call.durationMs !== undefined && ` · ${formatDuration(call.durationMs)}`}
        </span>
      </summary>
      <h3 className="mt-1 text-xs font-semibold uppercase text-slate-500 dark:text-slate-400">Arguments</h3>
      <pre className="whitespace-pre-wrap break-all text-xs">{prettyJson(call.argumentsJson)}</pre>
      {call.result !== undefined && (
        <>
          <h3 className="mt-1 text-xs font-semibold uppercase text-slate-500 dark:text-slate-400">
            {call.status === 'error' ? 'Error' : 'Result'}
          </h3>
          <pre className="max-h-64 overflow-y-auto whitespace-pre-wrap break-all text-xs">{call.result}</pre>
        </>
      )}
    </details>
  )
}
