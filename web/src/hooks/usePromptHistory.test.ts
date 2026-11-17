import { act, renderHook } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { usePromptHistory } from './usePromptHistory'

function setup(...sent: string[]) {
  const hook = renderHook(() => usePromptHistory())
  for (const p of sent) act(() => hook.result.current.record(p))
  return hook.result.current
}

describe('usePromptHistory', () => {
  it('has nothing to recall before the first prompt', () => {
    const h = setup()

    expect(h.previous('draft')).toBeUndefined()
    expect(h.next()).toBeUndefined()
  })

  it('walks back through older prompts and stops at the oldest', () => {
    const h = setup('one', 'two', 'three')

    expect(h.previous('')).toBe('three')
    expect(h.previous('')).toBe('two')
    expect(h.previous('')).toBe('one')
    expect(h.previous('')).toBe('one')
  })

  it('walks forward again and restores the unsent draft past the newest entry', () => {
    const h = setup('one', 'two')

    h.previous('half-typed')
    h.previous('ignored while browsing')
    expect(h.next()).toBe('two')
    expect(h.next()).toBe('half-typed')
    expect(h.next()).toBeUndefined()
  })

  it('does not store the same prompt twice in a row', () => {
    const h = setup('one', 'one')

    expect(h.previous('')).toBe('one')
    expect(h.previous('')).toBe('one')
    expect(h.next()).toBe('')
  })

  it('starts again from the newest prompt after a new prompt is recorded mid-browse', () => {
    const h = setup('one', 'two')
    h.previous('')
    h.previous('')

    h.record('three')

    expect(h.previous('')).toBe('three')
  })
})
