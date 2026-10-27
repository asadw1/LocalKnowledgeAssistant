import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('./index.css', () => ({}))
vi.mock('./App.tsx', () => ({ default: () => <p>app under test</p> }))

describe('main', () => {
  beforeEach(() => {
    vi.resetModules()
    document.body.innerHTML = '<div id="root"></div>'
  })

  it('mounts the app into #root', async () => {
    await import('./main.tsx')

    await vi.waitFor(() => expect(document.getElementById('root')).toHaveTextContent('app under test'))
  })
})
