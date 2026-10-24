import type { ThemeTerm } from "@anamnys/shared/lib/types";

export interface ThemeGroup {
  parent: ThemeTerm;
  children: ThemeTerm[];
}

const byLabel = (a: ThemeTerm, b: ThemeTerm) => a.label.localeCompare(b.label, "pt-BR");

// Top-level terms with their children; `include` decides which terms are listed (a group whose
// parent is excluded but has included children still appears, headed by the parent).
export function groupTerms(terms: ThemeTerm[], include: (term: ThemeTerm) => boolean): ThemeGroup[] {
  const top = terms.filter((t) => t.parentId === null).sort(byLabel);
  return top
    .map((parent) => ({
      parent,
      children: terms.filter((t) => t.parentId === parent.id && include(t)).sort(byLabel),
    }))
    .filter((g) => include(g.parent) || g.children.length > 0);
}
