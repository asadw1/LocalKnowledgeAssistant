import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { ToolCallData } from '../lib/transcript'
import { ToolCard } from './ToolCard'

const base: ToolCallData = { tool: 'sqlite-query', argumentsJson: '{"sql":"select 1"}', status: 'ok', result: '1 row', durationMs: 850 }

describe('ToolCard', () => {
  it('shows name, status and duration in the summary and is collapsed by default', () => {
    render(<ToolCard call={base} />)

    const card = screen.getByLabelText('Tool call sqlite-query')
    expect(card).not.toHaveAttribute('open')
    expect(card).toHaveTextContent('done · 850 ms')
  })

  it('shows durations of a second or more in seconds', () => {
    render(<ToolCard call={{ ...base, durationMs: 1234 }} />)

    expect(screen.getByLabelText('Tool call sqlite-query')).toHaveTextContent('done · 1.2 s')
  })

  it('pretty-prints JSON arguments and shows the result', () => {
    render(<ToolCard call={base} />)

    expect(screen.getByText(/"sql": "select 1"/)).toBeInTheDocument()
    expect(screen.getByText('Result')).toBeInTheDocument()
    expect(screen.getByText('1 row')).toBeInTheDocument()
  })

  it('keeps arguments that are not valid JSON as they are', () => {
    render(<ToolCard call={{ ...base, argumentsJson: 'not json' }} />)

    expect(screen.getByText('not json')).toBeInTheDocument()
  })

  it('marks an error and labels the result as an error', () => {
    render(<ToolCard call={{ ...base, status: 'error', result: 'boom' }} />)

    expect(screen.getByLabelText('Tool call sqlite-query')).toHaveTextContent('error')
    expect(screen.getByText('Error')).toBeInTheDocument()
  })

  it('shows a running call without result or duration', () => {
    render(<ToolCard call={{ tool: 't', argumentsJson: '{}', status: 'running' }} />)

    expect(screen.getByLabelText('Tool call t')).toHaveTextContent('running…')
    expect(screen.queryByText('Result')).not.toBeInTheDocument()
  })
})
