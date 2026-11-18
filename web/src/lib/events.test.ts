import { describe, expect, it } from 'vitest'
import { toAgentEvent } from './events'
import { StreamFormatError } from './sse'

describe('toAgentEvent', () => {
  it.each([
    ['RunStartedEvent', { RunId: 'abc' }],
    ['TokenEvent', { Text: 'hi' }],
    ['ToolCallEvent', { Tool: 't', ArgumentsJson: '{}' }],
    ['ToolResultEvent', { Tool: 't', Result: 'r', IsError: true }],
    ['ErrorEvent', { Message: 'boom' }],
    ['DoneEvent', {}],
  ])('accepts a well-formed %s', (type, data) => {
    expect(toAgentEvent(type, data)).toEqual({ type, ...data })
  })

  it.each(['FutureEvent', 'toString', '__proto__', 'constructor'])('ignores the unknown type %s', type => {
    expect(toAgentEvent(type, { a: 1 })).toBeNull()
  })

  it.each([
    ['TokenEvent', { Text: 5 }],
    ['TokenEvent', {}],
    ['ToolCallEvent', { Tool: 't' }],
    ['ToolResultEvent', { Tool: 't', Result: 'r', IsError: 'no' }],
    ['ToolResultEvent', { Tool: 't', Result: null, IsError: false }],
    ['ErrorEvent', { Message: undefined }],
    ['DoneEvent', null],
    ['DoneEvent', 'text'],
  ])('rejects a malformed %s', (type, data) => {
    expect(() => toAgentEvent(type, data)).toThrow(StreamFormatError)
    expect(() => toAgentEvent(type, data)).toThrow(`The server sent a malformed ${type}.`)
  })

  it('keeps extra fields a newer server may add', () => {
    expect(toAgentEvent('TokenEvent', { Text: 'x', Extra: 1 })).toEqual({ type: 'TokenEvent', Text: 'x', Extra: 1 })
  })
})
