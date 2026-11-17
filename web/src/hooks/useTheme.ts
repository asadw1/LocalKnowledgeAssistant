import { useEffect, useState } from 'react'

export type Theme = 'light' | 'dark'

const STORAGE_KEY = 'theme'

/** Stored choice first, then the OS preference. Storage can throw (blocked cookies), so it is never relied on. */
function initialTheme(): Theme {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    if (stored === 'light' || stored === 'dark') return stored
  } catch {
    // Fall through to the OS preference.
  }
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

/**
 * Light/dark theme state for the dashboard.
 *
 * Returns the current `theme` and a `toggle` function.
 * Side effects: adds or removes the `dark` class on `<html>` (Tailwind's `dark:` variant keys off it, see
 * index.css) and saves the choice to localStorage. index.html applies the same class before first paint
 * to avoid a flash; keep the two in step.
 */
export function useTheme() {
  const [theme, setTheme] = useState<Theme>(initialTheme)

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark')
    try {
      window.localStorage.setItem(STORAGE_KEY, theme)
    } catch {
      // The choice just will not persist.
    }
  }, [theme])

  return { theme, toggle: () => setTheme(t => (t === 'dark' ? 'light' : 'dark')) }
}
