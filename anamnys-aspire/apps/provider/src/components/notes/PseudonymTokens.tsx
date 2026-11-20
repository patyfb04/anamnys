import type { ReactNode } from "react";

const TOKEN = /(\[(?:P\d+|Empresa \d+|Escola \d+|Endereço)\])/g;

// Display only: wraps third-party codes in a highlight without changing the text itself.
export function highlightTokens(text: string): ReactNode {
  const parts = text.split(TOKEN);
  if (parts.length === 1) return text;
  return parts.map((part, i) =>
    i % 2 === 1 ? (
      <span key={i} className="rounded-sm bg-primaryFixed px-0.5 font-medium text-onPrimaryFixedVariant">
        {part}
      </span>
    ) : (
      part
    ),
  );
}
