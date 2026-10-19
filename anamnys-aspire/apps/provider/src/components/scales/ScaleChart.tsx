import { useTranslation } from "react-i18next";
import type { ScaleSeries } from "@anamnys/shared/lib/types";

const WIDTH = 320;
const HEIGHT = 120;
const PAD = { left: 34, right: 12, top: 10, bottom: 10 };

function formatDay(iso: string): string {
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", timeZone: "America/Sao_Paulo" }).format(
    new Date(iso),
  );
}

// One neutral series: score over time, y axis from the instrument's minScore to maxScore. Only
// those two bounds are labelled; there are no bands, cut-offs or status colours by design
// (the interpretation is the clinician's). Each point carries its date and score as a title.
export default function ScaleChart({ series }: { series: ScaleSeries }) {
  const { t } = useTranslation();
  const entries = [...series.entries].sort((a, b) => new Date(a.appliedAt).getTime() - new Date(b.appliedAt).getTime());
  if (entries.length === 0) return null;

  const { minScore, maxScore } = series;
  const span = maxScore - minScore || 1;
  const plotW = WIDTH - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;
  // Proportional to the date, so a long gap looks like a long gap; a single entry is centred.
  const times = entries.map((e) => new Date(e.appliedAt).getTime());
  const t0 = times[0];
  const tSpan = times[times.length - 1] - t0;
  const x = (i: number) =>
    entries.length === 1 || tSpan === 0 ? PAD.left + plotW / 2 : PAD.left + ((times[i] - t0) / tSpan) * plotW;
  const y = (score: number) => PAD.top + plotH - ((score - minScore) / span) * plotH;
  const points = entries.map((e, i) => ({ x: x(i), y: y(e.score), entry: e }));
  const path = points.map((p, i) => `${i === 0 ? "M" : "L"}${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(" ");

  return (
    <svg
      viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
      className="w-full max-w-md h-auto"
      role="img"
      aria-label={t("scales.chartLabel", { name: series.name })}
    >
      {/* Recessive baseline and top rule, no other grid */}
      <line x1={PAD.left} x2={WIDTH - PAD.right} y1={y(minScore)} y2={y(minScore)} className="stroke-outlineVariant" strokeWidth={1} />
      <line x1={PAD.left} x2={WIDTH - PAD.right} y1={y(maxScore)} y2={y(maxScore)} className="stroke-outlineVariant" strokeWidth={1} strokeDasharray="2 3" />
      <text x={PAD.left - 6} y={y(minScore)} textAnchor="end" dominantBaseline="middle" className="fill-onSurfaceVariant" fontSize={10}>
        {minScore}
      </text>
      <text x={PAD.left - 6} y={y(maxScore)} textAnchor="end" dominantBaseline="middle" className="fill-onSurfaceVariant" fontSize={10}>
        {maxScore}
      </text>
      {points.length > 1 && (
        <path d={path} fill="none" className="stroke-primary" strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
      )}
      {points.map((p) => (
        <circle key={p.entry.id} cx={p.x} cy={p.y} r={4} className="fill-primary stroke-surfaceContainerLowest" strokeWidth={2}>
          <title>{`${formatDay(p.entry.appliedAt)}: ${p.entry.score}`}</title>
        </circle>
      ))}
    </svg>
  );
}
