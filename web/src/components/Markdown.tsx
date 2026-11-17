import ReactMarkdown, { type Components } from 'react-markdown'
import remarkGfm from 'remark-gfm'

// Model output is untrusted. react-markdown does not render raw HTML and drops unsafe URL schemes such as
// javascript:. Images are replaced by their alt text: a model-written image URL would make the browser
// request an arbitrary address as soon as the answer renders (a data-leak channel under prompt injection).
const components: Components = {
  a: ({ href, children }) => (
    <a href={href} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  ),
  img: ({ alt }) => <span>{alt}</span>,
}

/**
 * Renders model text as formatted markdown (headings, bold, lists, tables, inline and fenced code).
 * Inputs: the markdown source; safe to call with partial text while an answer is still streaming.
 * Output: elements styled by the `.md` rules in index.css. Links open in a new tab; images are not loaded.
 * No side effects.
 */
export function Markdown({ children }: { children: string }) {
  return (
    <div className="md">
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {children}
      </ReactMarkdown>
    </div>
  )
}
