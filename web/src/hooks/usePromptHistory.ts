import { useRef } from 'react'

/**
 * Shell-style recall of previously sent prompts (ArrowUp = older, ArrowDown = newer).
 *
 * Inputs: none. Returns `record(prompt)` to store a sent prompt, `previous(current)` and `next()`,
 * each returning the text the input should now show, or `undefined` when there is nothing to move to.
 * Side effects: none outside the hook; history lives only in memory for the page's lifetime.
 * Whatever was being typed when the user first pressed ArrowUp is kept as a draft and restored
 * when ArrowDown moves past the newest entry.
 */
export function usePromptHistory() {
  const entries = useRef<string[]>([])
  // null means "not browsing": the input holds the user's own text (the draft).
  const cursor = useRef<number | null>(null)
  const draft = useRef('')

  function record(prompt: string) {
    // Consecutive duplicates would make ArrowUp appear stuck.
    if (entries.current.at(-1) !== prompt) entries.current.push(prompt)
    cursor.current = null
    draft.current = ''
  }

  function previous(current: string): string | undefined {
    if (entries.current.length === 0) return undefined
    if (cursor.current === null) {
      draft.current = current
      cursor.current = entries.current.length - 1
    } else if (cursor.current > 0) {
      cursor.current -= 1
    }
    return entries.current[cursor.current]
  }

  function next(): string | undefined {
    if (cursor.current === null) return undefined
    if (cursor.current < entries.current.length - 1) {
      cursor.current += 1
      return entries.current[cursor.current]
    }
    cursor.current = null
    return draft.current
  }

  return { record, previous, next }
}
