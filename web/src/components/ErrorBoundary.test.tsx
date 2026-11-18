import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ErrorBoundary } from './ErrorBoundary'

let broken = true
function Fragile() {
  if (broken) throw new Error('render failed')
  return <p>all good</p>
}

afterEach(() => {
  vi.restoreAllMocks()
  broken = true
})

describe('ErrorBoundary', () => {
  it('renders its children when nothing fails', () => {
    broken = false
    render(
      <ErrorBoundary>
        <Fragile />
      </ErrorBoundary>,
    )

    expect(screen.getByText('all good')).toBeInTheDocument()
  })

  it('shows an alert and logs the error when a child throws while rendering', () => {
    const log = vi.spyOn(console, 'error').mockImplementation(() => {})
    render(
      <ErrorBoundary>
        <Fragile />
      </ErrorBoundary>,
    )

    expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong')
    expect(screen.queryByText('render failed')).not.toBeInTheDocument() // details go to the console, not the page
    expect(log).toHaveBeenCalledWith('The dashboard failed to render.', expect.any(Error), expect.any(String))
  })

  it('renders the children again on "Try again", and shows the alert again if they still fail', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const user = userEvent.setup()
    render(
      <ErrorBoundary>
        <Fragile />
      </ErrorBoundary>,
    )

    await user.click(screen.getByRole('button', { name: 'Try again' }))
    expect(screen.getByRole('alert')).toBeInTheDocument()

    broken = false
    await user.click(screen.getByRole('button', { name: 'Try again' }))
    expect(screen.getByText('all good')).toBeInTheDocument()
  })
})
