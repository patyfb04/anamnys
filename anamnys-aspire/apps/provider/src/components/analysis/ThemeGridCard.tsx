import { useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ChevronRight, Rows3 } from "lucide-react";
import type { AnalysisOccurrence, AnalysisSession, AnalysisTheme } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import { inputClass } from "@/components/patients/FormField";
import { LANE_SORTS, buildLanes, relatives, sameSessionLabels, type CellItem, type LaneSort } from "./analysisModel";
import OccurrenceModal from "./OccurrenceModal";

const LABEL_W = "w-56 min-w-56";
const CELL_W = "w-14 min-w-14";

interface Open {
  on: string;
  noteId: string;
  items: CellItem[];
  sameSession: string[];
}

// "Temas por sessão": sessions as columns, one lane per term as recorded. Children sit indented under
// their parent; a collapsed parent marks a session when the parent or any child was recorded in it.
// One neutral mark colour; the orders offered are observation orders only.
export default function ThemeGridCard({ sessions, themes }: { sessions: AnalysisSession[]; themes: AnalysisTheme[] }) {
  const { t, i18n } = useTranslation();
  const [sort, setSort] = useState<LaneSort>("frequency");
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [open, setOpen] = useState<Open | null>(null);
  const lanes = useMemo(() => buildLanes(themes, sort, i18n.language), [themes, sort, i18n.language]);

  const toggle = (id: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (!next.delete(id)) next.add(id);
      return next;
    });

  // Visible rows in order; the cells form one grid with a single tab stop (arrow keys, Home, End move).
  const visible = useMemo(
    () =>
      lanes.flatMap((lane) => {
        const collapsed = !expanded.has(lane.theme.termId);
        return [
          { theme: lane.theme, depth: 0 as const, children: lane.children, collapsed },
          ...(lane.children.length > 0 && !collapsed
            ? lane.children.map((c) => ({ theme: c, depth: 1 as const, children: [] as AnalysisTheme[], collapsed: false }))
            : []),
        ];
      }),
    [lanes, expanded],
  );
  // termId -> noteId -> occurrences, so a cell is a lookup instead of a scan.
  const index = useMemo(() => {
    const m = new Map<string, Map<string, AnalysisOccurrence[]>>();
    for (const th of themes) {
      const byNote = new Map<string, AnalysisOccurrence[]>();
      for (const o of th.occurrences) byNote.set(o.noteId, [...(byNote.get(o.noteId) ?? []), o]);
      m.set(th.termId, byNote);
    }
    return m;
  }, [themes]);
  const dateFmt = useMemo(
    () => new Intl.DateTimeFormat(i18n.language, { day: "2-digit", month: "2-digit", year: "2-digit", timeZone: "UTC" }),
    [i18n.language],
  );
  // Column labels; sessions on the same day get an ordinal so names stay distinct.
  const columnLabels = useMemo(() => {
    const total = new Map<string, number>();
    for (const s of sessions) total.set(s.on, (total.get(s.on) ?? 0) + 1);
    const seen = new Map<string, number>();
    return sessions.map((s) => {
      const [y, mo, d] = s.on.split("-").map(Number);
      const label = dateFmt.format(Date.UTC(y, mo - 1, d));
      const n = (seen.get(s.on) ?? 0) + 1;
      seen.set(s.on, n);
      return (total.get(s.on) ?? 0) > 1 ? `${label} (${n})` : label;
    });
  }, [sessions, dateFmt]);
  const cellRefs = useRef(new Map<string, HTMLDivElement>());
  const [active, setActive] = useState({ r: 0, c: 0 });
  const ar = Math.min(active.r, Math.max(0, visible.length - 1));
  const ac = Math.min(active.c, Math.max(0, sessions.length - 1));
  const moveTo = (r: number, c: number) => {
    const nr = Math.max(0, Math.min(visible.length - 1, r));
    const nc = Math.max(0, Math.min(sessions.length - 1, c));
    setActive({ r: nr, c: nc });
    cellRefs.current.get(`${nr}:${nc}`)?.focus();
  };

  const row = (r: number, theme: AnalysisTheme, depth: 0 | 1, children: AnalysisTheme[], collapsed: boolean) => {
    const hasKids = depth === 0 && children.length > 0;
    const grouped = hasKids && collapsed;
    return (
      <div key={theme.termId} role="row" className="flex items-stretch border-t border-outlineVariant">
        <div
          role="rowheader"
          className={`${LABEL_W} sticky left-0 z-10 flex items-center gap-1 bg-surfaceContainerLowest py-1.5 pr-2 ${depth ? "pl-7" : "pl-1"}`}
        >
          {hasKids ? (
            <button
              type="button"
              onClick={() => toggle(theme.termId)}
              aria-expanded={!collapsed}
              aria-label={t(collapsed ? "analysis.themes.expand" : "analysis.themes.collapse", { label: theme.label })}
              className="rounded-radii-full p-1 text-onSurfaceVariant hover:bg-pastelLavender"
            >
              <ChevronRight size={16} className={`transition-transform ${collapsed ? "" : "rotate-90"}`} aria-hidden />
            </button>
          ) : (
            <span className="w-6" aria-hidden />
          )}
          <span className="min-w-0 break-words text-body-md text-onSurface">
            {theme.label}
            {!theme.active && <span className="text-body-sm text-onSurfaceVariant"> ({t("analysis.themes.inactive")})</span>}
            {theme.supersededBy.length > 0 && (
              <span className="block text-body-sm text-onSurfaceVariant">
                {t("analysis.themes.supersededBy", { labels: theme.supersededBy.map((s) => s.label).join(", ") })}
              </span>
            )}
          </span>
        </div>
        {sessions.map((s, c) => {
          const members = grouped ? [theme, ...children] : [theme];
          const items: CellItem[] = members.flatMap((m) =>
            (index.get(m.termId)?.get(s.noteId) ?? []).map((occurrence) => ({ theme: m, occurrence })),
          );
          const openCell = () =>
            items.length > 0 &&
            setOpen({
              on: s.on,
              noteId: s.noteId,
              items,
              sameSession: sameSessionLabels(themes, s.noteId, relatives(themes, theme)),
            });
          const onKeyDown = (e: React.KeyboardEvent) => {
            const move: Record<string, [number, number]> = {
              ArrowLeft: [r, c - 1],
              ArrowRight: [r, c + 1],
              ArrowUp: [r - 1, c],
              ArrowDown: [r + 1, c],
              Home: [r, 0],
              End: [r, sessions.length - 1],
            };
            if (move[e.key]) {
              e.preventDefault();
              moveTo(...move[e.key]);
            } else if (e.key === "Enter" || e.key === " ") {
              e.preventDefault();
              openCell();
            }
          };
          return (
            <div
              key={s.noteId}
              role="gridcell"
              ref={(el) => {
                if (el) cellRefs.current.set(`${r}:${c}`, el);
                else cellRefs.current.delete(`${r}:${c}`);
              }}
              tabIndex={r === ar && c === ac ? 0 : -1}
              aria-label={t(items.length > 0 ? "analysis.themes.cell" : "analysis.themes.cellEmpty", {
                label: theme.label,
                date: columnLabels[c],
              })}
              onFocus={() => setActive((a) => (a.r === r && a.c === c ? a : { r, c }))}
              onKeyDown={onKeyDown}
              onClick={openCell}
              className={`${CELL_W} flex items-center justify-center ${items.length > 0 ? "cursor-pointer hover:bg-pastelLavender" : ""}`}
            >
              {items.length > 0 && <span className="h-3 w-3 rounded-radii-full bg-onSurfaceVariant" aria-hidden />}
            </div>
          );
        })}
      </div>
    );
  };

  return (
    <DetailCard
      title={t("analysis.themes.title")}
      icon={<Rows3 size={20} />}
      action={
        <label className="flex items-center gap-2 text-label-md text-onSurfaceVariant">
          {t("analysis.themes.sortLabel")}
          <select className={`${inputClass} !w-auto`} value={sort} onChange={(e) => setSort(e.target.value as LaneSort)}>
            {LANE_SORTS.map((s) => (
              <option key={s} value={s}>
                {t(`analysis.themes.sort.${s}`)}
              </option>
            ))}
          </select>
        </label>
      }
    >
      {themes.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("analysis.themes.empty")}</p>
      ) : (
        <div className="flex flex-col gap-2">
          <div className="overflow-x-auto">
            <div role="grid" aria-label={t("analysis.themes.gridLabel", { count: sessions.length })} className="w-max min-w-full">
              <div role="row" className="flex items-end">
                <div role="columnheader" className={`${LABEL_W} sticky left-0 z-10 bg-surfaceContainerLowest py-1 text-label-md text-onSurfaceVariant`}>
                  {t("analysis.themes.themeColumn")}
                </div>
                {sessions.map((s, i) => (
                  <div key={s.noteId} role="columnheader" className={`${CELL_W} py-1 text-center text-label-sm text-onSurfaceVariant`}>
                    {columnLabels[i]}
                  </div>
                ))}
              </div>
              {visible.map((v, r) => row(r, v.theme, v.depth, v.children, v.collapsed))}
            </div>
          </div>
          {lanes.some((l) => l.children.length > 0) && (
            <p className="text-body-sm text-onSurfaceVariant">{t("analysis.themes.collapsedHint")}</p>
          )}
        </div>
      )}
      {open && <OccurrenceModal {...open} onClose={() => setOpen(null)} />}
    </DetailCard>
  );
}
