import { useTranslation } from "react-i18next";
import type { TimelineScale } from "@anamnys/shared/lib/types";
import AttendanceMark from "@/components/attendance/AttendanceMark";
import { DAY_MS, formatDay, monthTicks } from "@/components/timeline/timelineGeometry";
import { dashFor, type EventKind, type TrajectoryEvent, type TrajectoryPoint } from "./trajectoryModel";

export type TrajectorySelection = { kind: "point"; point: TrajectoryPoint } | { kind: "event"; event: TrajectoryEvent };

const WIDTH = 960;
const LEFT = 44;
const PAD_R = 16;
const AXIS_H = 24;
const EVENT_H = 30;
const PLOT_H = 170;
const PAD_Y = 10;
const MIN_WIDTH = 720;

/** Icon per event type, drawn around (cx, cy) in the one neutral colour. Shapes alone tell types apart. */
export function EventMark({ kind, cx, cy }: { kind: EventKind; cx: number; cy: number }) {
  const stroke = "stroke-onSurfaceVariant";
  const fill = "fill-onSurfaceVariant";
  switch (kind) {
    case "medication_start":
      return <polygon points={`${cx},${cy - 6} ${cx + 6},${cy + 5} ${cx - 6},${cy + 5}`} className={fill} />;
    case "medication_dose":
      return <polygon points={`${cx},${cy - 6} ${cx + 6},${cy} ${cx},${cy + 6} ${cx - 6},${cy}`} className={fill} />;
    case "medication_stop":
      return <rect x={cx - 5} y={cy - 5} width={10} height={10} className={fill} />;
    case "note":
      return (
        <>
          <rect x={cx - 4.5} y={cy - 6} width={9} height={12} rx={1.5} fill="none" className={stroke} strokeWidth={1.5} />
          <line x1={cx - 2} x2={cx + 2} y1={cy - 1.5} y2={cy - 1.5} className={stroke} strokeWidth={1.25} />
          <line x1={cx - 2} x2={cx + 2} y1={cy + 2} y2={cy + 2} className={stroke} strokeWidth={1.25} />
        </>
      );
    case "no_show":
    case "cancelled_provider":
    case "cancelled_patient":
      return <AttendanceMark kind={kind} cx={cx} cy={cy} />;
  }
}

// One instrument's scores as points joined by a line, with recorded events as vertical lines on the same
// date axis. One neutral colour, no bands, no trend line. Plain SVG like TimelineChart.
export default function TrajectoryChart({
  scale,
  points,
  events,
  startMs,
  endMs,
  selection,
  eventLabel,
  onSelect,
}: {
  scale: TimelineScale;
  points: TrajectoryPoint[];
  events: TrajectoryEvent[];
  startMs: number;
  endMs: number;
  selection: TrajectorySelection | null;
  eventLabel: (e: TrajectoryEvent) => string;
  onSelect: (s: TrajectorySelection) => void;
}) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const plotW = WIDTH - LEFT - PAD_R;
  const span = Math.max(endMs + DAY_MS - startMs, DAY_MS);
  const x = (ms: number) => LEFT + ((Math.min(Math.max(ms, startMs), endMs + DAY_MS) - startMs) / span) * plotW;
  const plotTop = AXIS_H + EVENT_H + PAD_Y;
  const plotH = PLOT_H - PAD_Y * 2;
  const height = AXIS_H + EVENT_H + PLOT_H + 6;
  const range = scale.maxScore - scale.minScore || 1;
  const y = (score: number) => plotTop + plotH - ((score - scale.minScore) / range) * plotH;
  const ticks = monthTicks(startMs, endMs, locale);
  const coords = points.map((p) => ({ p, cx: x(p.ms + DAY_MS / 2), cy: y(p.score) }));
  const path = coords.map((c, i) => `${i === 0 ? "M" : "L"}${c.cx.toFixed(1)} ${c.cy.toFixed(1)}`).join(" ");

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
        aria-label={t("analysis.trajectory.chartLabel", {
          name: scale.name,
          from: formatDay(new Date(startMs).toISOString(), locale),
          to: formatDay(new Date(endMs).toISOString(), locale),
        })}
      >
        <line x1={LEFT} x2={WIDTH - PAD_R} y1={AXIS_H - 4} y2={AXIS_H - 4} className="stroke-outlineVariant" strokeWidth={1} />
        {ticks.map((tk) => (
          <g key={tk.ms}>
            <line x1={x(tk.ms)} x2={x(tk.ms)} y1={AXIS_H - 4} y2={height - 6} className="stroke-outlineVariant" strokeWidth={1} strokeDasharray="2 4" opacity={0.6} />
            <text x={x(tk.ms)} y={AXIS_H - 10} textAnchor="middle" className="fill-onSurfaceVariant" fontSize={10}>
              {tk.label}
            </text>
          </g>
        ))}

        {/* Score axis: instrument minimum and maximum only */}
        <line x1={LEFT} x2={WIDTH - PAD_R} y1={y(scale.minScore)} y2={y(scale.minScore)} className="stroke-outlineVariant" strokeWidth={1} />
        <line x1={LEFT} x2={WIDTH - PAD_R} y1={y(scale.maxScore)} y2={y(scale.maxScore)} className="stroke-outlineVariant" strokeWidth={1} strokeDasharray="2 3" />
        <text x={LEFT - 6} y={y(scale.minScore)} textAnchor="end" dominantBaseline="middle" className="fill-onSurfaceVariant" fontSize={10}>
          {scale.minScore}
        </text>
        <text x={LEFT - 6} y={y(scale.maxScore)} textAnchor="end" dominantBaseline="middle" className="fill-onSurfaceVariant" fontSize={10}>
          {scale.maxScore}
        </text>

        {/* Event lines behind the scores */}
        {events.map((ev) => {
          const same = events.filter((o) => o.ms === ev.ms);
          const slot = same.findIndex((o) => o.id === ev.id);
          const lx = x(ev.ms + DAY_MS / 2);
          const ix = lx + (slot - (same.length - 1) / 2) * 16;
          const cy = AXIS_H + EVENT_H / 2;
          const selected = selection?.kind === "event" && selection.event.id === ev.id;
          const label = eventLabel(ev);
          const pick = () => onSelect({ kind: "event", event: ev });
          return (
            <g key={ev.id}>
              <line x1={lx} x2={lx} y1={AXIS_H + EVENT_H - 4} y2={PLOT_H + AXIS_H + EVENT_H} className="stroke-onSurfaceVariant" strokeWidth={1} strokeDasharray={dashFor(ev.kind)} opacity={0.6} />
              <g
                role="button"
                tabIndex={0}
                aria-label={label}
                aria-pressed={selected}
                className="cursor-pointer outline-none [&:focus-visible>rect]:stroke-primary"
                onClick={pick}
                onKeyDown={(e) => activate(e, pick)}
              >
                <rect
                  x={ix - 10}
                  y={cy - 12}
                  width={20}
                  height={24}
                  rx={4}
                  className={selected ? "fill-pastelLavender stroke-primary" : "fill-transparent stroke-transparent hover:fill-pastelLavender"}
                  strokeWidth={1.5}
                />
                <EventMark kind={ev.kind} cx={ix} cy={cy} />
                <title>{`${formatDay(ev.on, locale)}: ${label}`}</title>
              </g>
            </g>
          );
        })}

        {coords.length > 1 && <path d={path} fill="none" className="stroke-primary" strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />}
        {coords.map(({ p, cx, cy }) => {
          const selected = selection?.kind === "point" && selection.point.id === p.id;
          const pick = () => onSelect({ kind: "point", point: p });
          return (
            <circle
              key={p.id}
              cx={cx}
              cy={cy}
              r={selected ? 6 : 4.5}
              role="button"
              tabIndex={0}
              aria-label={t("timeline.pointLabel", { name: scale.name, score: p.score, date: formatDay(p.on, locale) })}
              aria-pressed={selected}
              className="cursor-pointer fill-primary stroke-surfaceContainerLowest outline-none hover:fill-primary/70 focus-visible:stroke-onSurface"
              strokeWidth={2}
              onClick={pick}
              onKeyDown={(e) => activate(e, pick)}
            >
              <title>{`${formatDay(p.on, locale)}: ${p.score}`}</title>
            </circle>
          );
        })}
      </svg>
    </div>
  );
}
