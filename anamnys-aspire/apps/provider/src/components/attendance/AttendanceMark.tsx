import type { AttendanceKind } from "@anamnys/shared/lib/types";

const ATTENDANCE_KINDS: AttendanceKind[] = [
  "attended",
  "attended_unscheduled",
  "no_show",
  "cancelled_provider",
  "cancelled_patient",
  "unrecorded",
];

const R = 5;

// One neutral colour for every kind: the shape alone tells them apart, so no status reads as good or bad.
// Drawn around (cx, cy) so the timeline lane and the legend share it.
export default function AttendanceMark({ kind, cx, cy }: { kind: AttendanceKind; cx: number; cy: number }) {
  const stroke = "stroke-onSurfaceVariant";
  const fill = "fill-onSurfaceVariant";
  const cross = (
    <>
      <line x1={cx - 3.5} x2={cx + 3.5} y1={cy - 3.5} y2={cy + 3.5} className={stroke} strokeWidth={1.75} strokeLinecap="round" />
      <line x1={cx - 3.5} x2={cx + 3.5} y1={cy + 3.5} y2={cy - 3.5} className={stroke} strokeWidth={1.75} strokeLinecap="round" />
    </>
  );
  switch (kind) {
    case "attended":
      return <circle cx={cx} cy={cy} r={R} className={fill} />;
    case "attended_unscheduled":
      return (
        <>
          <circle cx={cx} cy={cy} r={R + 2} fill="none" className={stroke} strokeWidth={1.5} />
          <circle cx={cx} cy={cy} r={R - 2} className={fill} />
        </>
      );
    case "no_show":
      return <circle cx={cx} cy={cy} r={R} fill="none" className={stroke} strokeWidth={1.75} />;
    case "cancelled_provider":
      return cross;
    case "cancelled_patient":
      return (
        <>
          <circle cx={cx} cy={cy} r={R + 2} fill="none" className={stroke} strokeWidth={1.5} />
          {cross}
        </>
      );
    case "unrecorded":
      return <line x1={cx - 5} x2={cx + 5} y1={cy} y2={cy} className={stroke} strokeWidth={2} strokeLinecap="round" />;
  }
}

export function AttendanceLegend({ label }: { label: (kind: AttendanceKind) => string }) {
  return (
    <ul className="flex flex-wrap gap-x-4 gap-y-1">
      {ATTENDANCE_KINDS.map((k) => (
        <li key={k} className="flex items-center gap-1.5 text-body-sm text-onSurfaceVariant">
          <svg width={18} height={18} viewBox="0 0 18 18" aria-hidden>
            <AttendanceMark kind={k} cx={9} cy={9} />
          </svg>
          {label(k)}
        </li>
      ))}
    </ul>
  );
}
