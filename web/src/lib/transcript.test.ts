import { describe, expect, it } from 'vitest'
import type { AgentEvent } from '../hooks/useAgentStream'
import { splitAnswer } from './transcript'

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
