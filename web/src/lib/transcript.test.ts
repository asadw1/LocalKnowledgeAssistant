import { describe, expect, it } from 'vitest'
import type { AgentEvent } from '../hooks/useAgentStream'
import { buildToolCalls, splitAnswer } from './transcript'

const token = (Text: string): AgentEvent => ({ type: 'TokenEvent', Text })
const call: AgentEvent = { type: 'ToolCallEvent', Tool: 'sqlite-query', ArgumentsJson: '{}' }
const result: AgentEvent = { type: 'ToolResultEvent', Tool: 'sqlite-query', Result: 'x', IsError: false }

describe('splitAnswer', () => {
  it('returns nothing for no events', () => {
    expect(splitAnswer([])).toEqual({ answer: '', notes: [] })
  })

  it('treats all tokens as the answer when no tool was called', () => {
    expect(splitAnswer([token('Hel'), token('lo'), { type: 'DoneEvent' }])).toEqual({ answer: 'Hello', notes: [] })
  })

  it('moves text before each tool call into notes and keeps only the final segment as the answer', () => {
    const events = [token("Let's query. "), call, result, token('Still looking.'), call, result, token('The answer.')]

    expect(splitAnswer(events)).toEqual({ answer: 'The answer.', notes: ["Let's query.", 'Still looking.'] })
  })

  it('drops blank segments, for example a tool call with no text before it', () => {
    expect(splitAnswer([call, result, token('  '), call, result, token('Done')])).toEqual({ answer: 'Done', notes: [] })
  })

  it('leaves the answer empty when the run ends right after a tool result', () => {
    expect(splitAnswer([token('Thinking'), call, result, { type: 'ErrorEvent', Message: 'cap' }])).toEqual({
      answer: '',
      notes: ['Thinking'],
    })
  })
})

describe('buildToolCalls', () => {
  const callAt = (Tool: string, at?: number): AgentEvent => ({ type: 'ToolCallEvent', Tool, ArgumentsJson: '{"a":1}', at })
  const resultAt = (Tool: string, IsError: boolean, at?: number): AgentEvent => ({
    type: 'ToolResultEvent',
    Tool,
    Result: 'r',
    IsError,
    at,
  })

  it('returns nothing for no events', () => {
    expect(buildToolCalls([])).toEqual([])
  })

  it('pairs a call with its result and measures the duration', () => {
    expect(buildToolCalls([callAt('t', 100), resultAt('t', false, 350)])).toEqual([
      { tool: 't', argumentsJson: '{"a":1}', status: 'ok', result: 'r', durationMs: 250 },
    ])
  })

  it('marks a call without a result as running', () => {
    expect(buildToolCalls([callAt('t', 100)])).toEqual([{ tool: 't', argumentsJson: '{"a":1}', status: 'running' }])
  })

  it('marks an error result and pairs repeated calls to the same tool in order', () => {
    const calls = buildToolCalls([callAt('t', 0), resultAt('t', true, 10), callAt('t', 20), resultAt('t', false, 30)])

    expect(calls.map(c => c.status)).toEqual(['error', 'ok'])
    expect(calls.map(c => c.durationMs)).toEqual([10, 10])
  })

  it('omits the duration when timestamps are missing and clamps a negative one to zero', () => {
    expect(buildToolCalls([callAt('t'), resultAt('t', false, 5)])[0].durationMs).toBeUndefined()
    expect(buildToolCalls([callAt('t', 5), resultAt('t', false)])[0].durationMs).toBeUndefined()
    expect(buildToolCalls([callAt('t', 9), resultAt('t', false, 5)])[0].durationMs).toBe(0)
  })

  it('ignores a result with no open call', () => {
    expect(buildToolCalls([resultAt('t', false, 1), callAt('u', 2), resultAt('t', false, 3)])).toEqual([
      { tool: 'u', argumentsJson: '{"a":1}', status: 'running' },
    ])
  })
})
