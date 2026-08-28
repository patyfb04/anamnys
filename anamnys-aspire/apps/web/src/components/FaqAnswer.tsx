import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";

// Small-scale sibling of LegalDocument — same idea (render the real markdown instead of
// hand-transcribing every bold/italic span into i18n keys), but sized for a single FAQ
// answer: no headings/tables, just paragraphs/bold/italic at the page's smaller text size.
const components: Components = {
  p: ({ children }) => <p className="text-body-md text-onSurfaceVariant leading-relaxed mb-2.5 last:mb-0">{children}</p>,
  strong: ({ children }) => <strong className="font-bold">{children}</strong>,
  em: ({ children }) => <em className="italic">{children}</em>,
  ul: ({ children }) => <ul className="list-disc pl-5 mb-2.5 space-y-1 text-body-md text-onSurfaceVariant">{children}</ul>,
  li: ({ children }) => <li>{children}</li>,
};

export default function FaqAnswer({ markdown }: { markdown: string }) {
  return (
    <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
      {markdown}
    </ReactMarkdown>
  );
}
