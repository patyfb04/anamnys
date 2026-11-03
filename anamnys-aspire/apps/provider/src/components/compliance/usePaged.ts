import { useState } from "react";

export const COMPLIANCE_PAGE_SIZE = 5;

// One page of an already loaded list; the page is kept in range when the list shrinks (a discard, a signature).
export function usePaged<T>(items: T[], size = COMPLIANCE_PAGE_SIZE) {
  const [page, setPage] = useState(0);
  const pages = Math.max(1, Math.ceil(items.length / size));
  const current = Math.min(page, pages - 1);
  const first = current * size;
  return { shown: items.slice(first, first + size), page: current, pages, first, total: items.length, setPage };
}
