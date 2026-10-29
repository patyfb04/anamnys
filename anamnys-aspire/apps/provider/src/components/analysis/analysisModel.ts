import type { AnalysisOccurrence, AnalysisTheme } from "@anamnys/shared/lib/types";

export type LaneSort = "frequency" | "recency" | "alphabetical";
export const LANE_SORTS: LaneSort[] = ["frequency", "recency", "alphabetical"];

export interface Lane {
  theme: AnalysisTheme;
  depth: 0 | 1;
  /** Child themes of a top-level lane (empty for a child lane). */
  children: AnalysisTheme[];
}

const sessionCount = (themes: AnalysisTheme[]) => new Set(themes.flatMap((t) => t.occurrences.map((o) => o.noteId))).size;
const lastOn = (themes: AnalysisTheme[]) => themes.flatMap((t) => t.occurrences.map((o) => o.on)).sort().reverse()[0] ?? "";

function sorter(sort: LaneSort, locale: string) {
  return (a: AnalysisTheme[], b: AnalysisTheme[]) => {
    if (sort === "frequency") {
      const d = sessionCount(b) - sessionCount(a);
      if (d !== 0) return d;
    } else if (sort === "recency") {
      const x = lastOn(a);
      const y = lastOn(b);
      if (x !== y) return x < y ? 1 : -1;
    }
    return a[0].label.localeCompare(b[0].label, locale);
  };
}

/**
 * Top-level lanes (a term without a listed parent) with their children. A top-level lane is ranked by
 * itself plus its children; children are ranked among siblings. Only observation orders are offered.
 */
export function buildLanes(themes: AnalysisTheme[], sort: LaneSort, locale: string): Lane[] {
  const ids = new Set(themes.map((t) => t.termId));
  const childrenOf = new Map<string, AnalysisTheme[]>();
  const roots: AnalysisTheme[] = [];
  for (const t of themes) {
    if (t.parentId && ids.has(t.parentId)) childrenOf.set(t.parentId, [...(childrenOf.get(t.parentId) ?? []), t]);
    else roots.push(t);
  }
  const cmp = sorter(sort, locale);
  const byGroup = (a: AnalysisTheme, b: AnalysisTheme) => cmp([a, ...(childrenOf.get(a.termId) ?? [])], [b, ...(childrenOf.get(b.termId) ?? [])]);
  return roots.sort(byGroup).map((theme) => ({
    theme,
    depth: 0,
    children: (childrenOf.get(theme.termId) ?? []).sort((a, b) => cmp([a], [b])),
  }));
}

/** Occurrences of a lane in a session; a collapsed parent includes its children's. */
export function cellOccurrences(lane: { theme: AnalysisTheme; children: AnalysisTheme[] }, collapsed: boolean, noteId: string) {
  const members = collapsed ? [lane.theme, ...lane.children] : [lane.theme];
  return members.flatMap((t) => t.occurrences.filter((o) => o.noteId === noteId).map((o) => ({ theme: t, occurrence: o })));
}

export type CellItem = { theme: AnalysisTheme; occurrence: AnalysisOccurrence };

/** The term itself, its ancestors and its descendants. */
export function relatives(themes: AnalysisTheme[], theme: AnalysisTheme): AnalysisTheme[] {
  const byId = new Map(themes.map((t) => [t.termId, t]));
  const out = new Map<string, AnalysisTheme>([[theme.termId, theme]]);
  for (let p = theme.parentId ? byId.get(theme.parentId) : undefined; p && !out.has(p.termId); p = p.parentId ? byId.get(p.parentId) : undefined)
    out.set(p.termId, p);
  let grew = true;
  while (grew) {
    grew = false;
    for (const t of themes)
      if (!out.has(t.termId) && t.parentId && out.has(t.parentId)) {
        out.set(t.termId, t);
        grew = true;
      }
  }
  return [...out.values()];
}

/** Labels of the other themes recorded in the same session, excluding the given lane's members. */
export function sameSessionLabels(themes: AnalysisTheme[], noteId: string, exclude: AnalysisTheme[]): string[] {
  const skip = new Set(exclude.map((t) => t.termId));
  return themes.filter((t) => !skip.has(t.termId) && t.occurrences.some((o) => o.noteId === noteId)).map((t) => t.label);
}
