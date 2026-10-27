import { useTranslation } from "react-i18next";
import type { PatientTimeline, TimelineScale, TimelineSegment } from "@anamnys/shared/lib/types";
import { DAY_MS, dayMs, formatDay, monthTicks } from "./timelineGeometry";

export type TimelineSelection =
  | { kind: "segment"; drug: string; segment: TimelineSegment }
  | { kind: "point"; scale: TimelineScale; point: TimelineScale["points"][number] };

const WIDTH = 960;
const LABEL_W = 160;
const PAD_R = 16;
const AXIS_H = 24;
const SECTION_H = 22;
const LANE_H = 30;
const BAR_H = 18;
const SCALE_H = 72;
const SCALE_PAD = 10;
const MIN_WIDTH = 720;

function clip(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

// One shared date axis for medication lanes (a bar per dose, open-ended to today) and scale rows
// (connected points). Colours are neutral on purpose: nothing here states a relation between a
// drug and a score, and there are no bands or status colours. Plain SVG, like ScaleChart.
export default function TimelineChart({
  data,
  startMs,
  endMs,
  selection,
  onSelect,
}: {
  data: PatientTimeline;
  startMs: number;
  endMs: number;
  selection: TimelineSelection | null;
  onSelect: (s: TimelineSelection) => void;
}) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const plotW = WIDTH - LABEL_W - PAD_R;
  const span = Math.max(endMs - startMs, DAY_MS);
  const x = (ms: number) => LABEL_W + ((Math.min(Math.max(ms, startMs), endMs) - startMs) / span) * plotW;
  const ticks = monthTicks(startMs, endMs, locale);

  const medSection = data.medications.length > 0;
  const scaleSection = data.scales.length > 0;
  let y = AXIS_H;
  const medTop = y;
  if (medSection) y += SECTION_H + data.medications.length * LANE_H;
  const scaleTop = y;
  if (scaleSection) y += SECTION_H + data.scales.length * SCALE_H;
  const height = y + 6;

  const activate = (e: React.KeyboardEvent, fn: () => void) => {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      fn();
    }
  };

  return (
    <div className="overflow-x-auto">
      <svg
        viewBox={`0 0 ${WIDTH} ${height}`}
        style={{ minWidth: MIN_WIDTH }}
        className="h-auto w-full"
        role="group"
        aria-label={t("timeline.chartLabel", { from: formatDay(new Date(startMs).toISOString(), locale), to: formatDay(new Date(endMs).toISOString(), locale) })}
      >
        {/* Shared date axis: month ticks with faint guide lines through every row */}
        <line x1={LABEL_W} x2={WIDTH - PAD_R} y1={AXIS_H - 4} y2={AXIS_H - 4} className="stroke-outlineVariant" strokeWidth={1} />
        {ticks.map((tk) => (
          <g key={tk.ms}>
            <line x1={x(tk.ms)} x2={x(tk.ms)} y1={AXIS_H - 4} y2={height - 6} className="stroke-outlineVariant" strokeWidth={1} strokeDasharray="2 4" opacity={0.6} />
            <text x={x(tk.ms)} y={AXIS_H - 10} textAnchor="middle" className="fill-onSurfaceVariant" fontSize={10}>
              {tk.label}
            </text>
          </g>
        ))}

        {medSection && (
          <g>
            <text x={0} y={medTop + 14} className="fill-onSurfaceVariant" fontSize={11} fontWeight={600}>
              {t("timeline.medications")}
            </text>
            {data.medications.map((lane, li) => {
              const top = medTop + SECTION_H + li * LANE_H;
              return (
                <g key={lane.treatmentId}>
                  <text x={0} y={top + LANE_H / 2} dominantBaseline="middle" className="fill-onSurface" fontSize={12}>
                    {clip(lane.drug, 22)}
                    <title>{lane.drug}</title>
                  </text>
                  {lane.segments.map((seg, si) => {
                    const sMs = dayMs(seg.startedOn);
                    const eMs = seg.endedOn ? dayMs(seg.endedOn) + DAY_MS : endMs;
                    if (eMs < startMs || sMs > endMs) return null;
                    const x0 = x(sMs);
                    const w = Math.max(x(eMs) - x0, 3);
                    const label = seg.dose ?? t("timeline.noDose");
                    const selected = selection?.kind === "segment" && selection.segment.medicationId === seg.medicationId;
                    const pick = () => onSelect({ kind: "segment", drug: lane.drug, segment: seg });
                    return (
                      <g
                        key={seg.medicationId}
                        role="button"
                        tabIndex={0}
                        aria-label={t("timeline.segmentLabel", { drug: lane.drug, dose: label, start: formatDay(seg.startedOn, locale) })}
                        aria-pressed={selected}
                        className="cursor-pointer outline-none [&:focus-visible>rect]:stroke-primary"
                        onClick={pick}
                        onKeyDown={(e) => activate(e, pick)}
                      >
                        <rect
                          x={x0}
                          y={top + (LANE_H - BAR_H) / 2}
                          width={w}
                          height={BAR_H}
                          rx={4}
                          strokeWidth={selected ? 2 : 1}
                          className={`stroke-primary ${si % 2 === 0 ? "fill-primary/20 hover:fill-primary/30" : "fill-primary/35 hover:fill-primary/45"}`}
                          strokeOpacity={selected ? 1 : 0.5}
                        />
                        {w > label.length * 6 + 8 && (
                          <text x={x0 + 6} y={top + LANE_H / 2} dominantBaseline="middle" fontSize={10} className="pointer-events-none fill-onSurface">
                            {label}
                          </text>
                        )}
                      </g>
                    );
                  })}
                </g>
              );
            })}
          </g>
        )}

        {scaleSection && (
          <g>
            <text x={0} y={scaleTop + 14} className="fill-onSurfaceVariant" fontSize={11} fontWeight={600}>
              {t("timeline.scales")}
            </text>
            {data.scales.map((sc, si) => {
              const top = scaleTop + SECTION_H + si * SCALE_H;
              const plotTop = top + SCALE_PAD;
              const plotH = SCALE_H - SCALE_PAD * 2;
              const range = sc.maxScore - sc.minScore || 1;
              const y2 = (score: number) => plotTop + plotH - ((score - sc.minScore) / range) * plotH;
              const pts = [...sc.points].sort((a, b) => dayMs(a.appliedOn) - dayMs(b.appliedOn));
              const coords = pts.map((p) => ({ p, cx: x(dayMs(p.appliedOn) + DAY_MS / 2), cy: y2(p.score) }));
              const path = coords.map((c, i) => `${i === 0 ? "M" : "L"}${c.cx.toFixed(1)} ${c.cy.toFixed(1)}`).join(" ");
              return (
                <g key={sc.instrumentId}>
                  <text x={0} y={top + SCALE_H / 2 - 6} dominantBaseline="middle" className="fill-onSurface" fontSize={12}>
                    {clip(sc.name, 22)}
                    <title>{sc.name}</title>
                  </text>
                  <text x={0} y={top + SCALE_H / 2 + 10} dominantBaseline="middle" className="fill-onSurfaceVariant" fontSize={10}>
                    {sc.code} · {sc.minScore}–{sc.maxScore}
                  </text>
                  {/* Recessive baseline and top rule, same as ScaleChart; no bands or cut-offs */}
                  <line x1={LABEL_W} x2={WIDTH - PAD_R} y1={y2(sc.minScore)} y2={y2(sc.minScore)} className="stroke-outlineVariant" strokeWidth={1} />
                  <line x1={LABEL_W} x2={WIDTH - PAD_R} y1={y2(sc.maxScore)} y2={y2(sc.maxScore)} className="stroke-outlineVariant" strokeWidth={1} strokeDasharray="2 3" />
                  {coords.length > 1 && (
                    <path d={path} fill="none" className="stroke-primary" strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
                  )}
                  {coords.map(({ p, cx, cy }) => {
                    const selected = selection?.kind === "point" && selection.point.applicationId === p.applicationId;
                    const pick = () => onSelect({ kind: "point", scale: sc, point: p });
                    return (
                      <circle
                        key={p.applicationId}
                        cx={cx}
                        cy={cy}
                        r={selected ? 6 : 4.5}
                        role="button"
                        tabIndex={0}
                        aria-label={t("timeline.pointLabel", { name: sc.name, score: p.score, date: formatDay(p.appliedOn, locale) })}
                        aria-pressed={selected}
                        className="cursor-pointer fill-primary stroke-surfaceContainerLowest outline-none hover:fill-primary/70 focus-visible:stroke-onSurface"
                        strokeWidth={2}
                        onClick={pick}
                        onKeyDown={(e) => activate(e, pick)}
                      >
                        <title>{`${formatDay(p.appliedOn, locale)}: ${p.score}`}</title>
                      </circle>
                    );
                  })}
                </g>
              );
            })}
          </g>
        )}
      </svg>
    </div>
  );
}
