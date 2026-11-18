import { Component, type ErrorInfo, type ReactNode } from 'react'

/**
 * Catches an error thrown while rendering any descendant and shows a message instead of a blank page.
 * Inputs: the children to protect. Output: the children, or an alert with a "Try again" button after a render error.
 * Side effects: logs the error and component stack to the console; "Try again" clears the failed state and renders
 * the children again (so it recovers only if whatever threw has since changed).
 * Does not catch errors in event handlers or async code; those are handled where they happen.
 */
export class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('The dashboard failed to render.', error, info.componentStack)
  }

  render() {
    if (!this.state.failed) return this.props.children
    return (
      <div role="alert" className="m-6 rounded bg-red-50 p-4 text-red-700 dark:bg-red-950 dark:text-red-300">
        <p>Something went wrong while showing this page. The details are in the browser console.</p>
        <button
          type="button"
          onClick={() => this.setState({ failed: false })}
          className="mt-2 rounded-lg border border-red-300 px-3 py-1 text-sm dark:border-red-800"
        >
          Try again
        </button>
      </div>
    )
  }
}
