import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";

// Renders one of the legal source docs (src/content/legal/*.md — Privacy Policy, Terms of
// Service) with this app's own typography tokens instead of a generic "prose" plugin, so
// legal pages read as part of the same design system as everything else. remark-gfm is
// required for the pipe tables both documents use throughout.
const components: Components = {
  h1: ({ children }) => <h1 className="text-headline-lg text-[28px] md:text-[32px] text-primary mt-10 mb-4 first:mt-0">{children}</h1>,
  h2: ({ children }) => (
    <h2 className="text-headline-md text-[22px] text-primary mt-10 mb-3 pt-6 border-t border-outlineVariant first:mt-0 first:pt-0 first:border-0">
      {children}
    </h2>
  ),
  h3: ({ children }) => <h3 className="text-headline-sm text-[17px] text-primary mt-6 mb-2">{children}</h3>,
  p: ({ children }) => <p className="text-body-lg text-onSurfaceVariant leading-relaxed mb-4">{children}</p>,
  strong: ({ children }) => <strong className="font-bold text-onSurface">{children}</strong>,
  em: ({ children }) => <em className="italic">{children}</em>,
  a: ({ href, children }) => (
    <a href={href} className="text-primary underline hover:opacity-80 transition-opacity">
      {children}
    </a>
  ),
  ul: ({ children }) => <ul className="list-disc pl-6 mb-4 space-y-1.5 text-body-lg text-onSurfaceVariant">{children}</ul>,
  ol: ({ children }) => <ol className="list-decimal pl-6 mb-4 space-y-1.5 text-body-lg text-onSurfaceVariant">{children}</ol>,
  li: ({ children }) => <li className="leading-relaxed">{children}</li>,
  hr: () => <hr className="border-outlineVariant my-8" />,
  blockquote: ({ children }) => (
    <blockquote className="border-l-4 border-primary bg-primaryFixed/40 rounded-radii-md px-4 py-3 mb-4 text-body-lg text-onSurface italic">
      {children}
    </blockquote>
  ),
  code: ({ children }) => <code className="bg-surfaceContainerHigh rounded px-1.5 py-0.5 text-[13px] font-mono text-onSurface">{children}</code>,
  table: ({ children }) => (
    <div className="overflow-x-auto mb-6 rounded-radii-md border border-outlineVariant">
      <table className="w-full text-left border-collapse">{children}</table>
    </div>
  ),
  thead: ({ children }) => <thead className="bg-surfaceContainerLow">{children}</thead>,
  th: ({ children }) => (
    <th className="text-label-md text-onSurfaceVariant uppercase px-3.5 py-2.5 border-b border-outlineVariant align-top">{children}</th>
  ),
  td: ({ children }) => (
    <td className="text-body-md text-onSurfaceVariant px-3.5 py-2.5 border-b border-outlineVariant align-top">{children}</td>
  ),
  tr: ({ children }) => <tr className="last:[&>td]:border-b-0">{children}</tr>,
};

export default function LegalDocument({ markdown }: { markdown: string }) {
  return (
    <div className="max-w-3xl mx-auto px-4 py-16">
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {markdown}
      </ReactMarkdown>
    </div>
  );
}
