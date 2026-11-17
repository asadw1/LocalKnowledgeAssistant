import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useTheme } from './useTheme'

const prefersDark = (matches: boolean) =>
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches })))

beforeEach(() => {
  window.localStorage.clear()
  document.documentElement.classList.remove('dark')
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('useTheme', () => {
  it('defaults to light when there is no saved choice and no OS preference API', () => {
    const { result } = renderHook(() => useTheme())

    expect(result.current.theme).toBe('light')
    expect(document.documentElement).not.toHaveClass('dark')
  })

  it('follows the OS preference when nothing is saved', () => {
    prefersDark(true)

    const { result } = renderHook(() => useTheme())

    expect(result.current.theme).toBe('dark')
    expect(document.documentElement).toHaveClass('dark')
  })

  it('stays light when the OS prefers light', () => {
    prefersDark(false)

    expect(renderHook(() => useTheme()).result.current.theme).toBe('light')
  })

  it('prefers a saved choice over the OS setting', () => {
    prefersDark(true)
    window.localStorage.setItem('theme', 'light')

    expect(renderHook(() => useTheme()).result.current.theme).toBe('light')
  })

  it('ignores a junk saved value', () => {
    window.localStorage.setItem('theme', 'purple')

    expect(renderHook(() => useTheme()).result.current.theme).toBe('light')
  })

  it('toggles both ways, updating the html class and the saved choice', () => {
    const { result } = renderHook(() => useTheme())

    act(() => result.current.toggle())
    expect(result.current.theme).toBe('dark')
    expect(document.documentElement).toHaveClass('dark')
    expect(window.localStorage.getItem('theme')).toBe('dark')

    act(() => result.current.toggle())
    expect(result.current.theme).toBe('light')
    expect(document.documentElement).not.toHaveClass('dark')
    expect(window.localStorage.getItem('theme')).toBe('light')
  })

  it('still works when storage is unavailable', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked')
    })

    const { result } = renderHook(() => useTheme())
    act(() => result.current.toggle())

    expect(result.current.theme).toBe('dark')
    expect(document.documentElement).toHaveClass('dark')
  })
})
