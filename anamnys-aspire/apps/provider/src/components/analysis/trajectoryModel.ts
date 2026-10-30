import type { AttendanceKind, CaseAnalysis, PatientTimeline, TimelineScale } from "@anamnys/shared/lib/types";
import { DAY_MS, dayMs } from "@/components/timeline/timelineGeometry";

// Pure model for the scale trajectory card (F-13). No React: kept testable for when the app gets a test runner.
// It only lays facts out on a date axis; nothing here relates an event to a score.

export type EventGroup = "medication" | "notes" | "absences";
export type EventKind = "medication_start" | "medication_dose" | "medication_stop" | "note" | "no_show" | "cancelled_provider" | "cancelled_patient";

export const EVENT_GROUPS: EventGroup[] = ["medication", "notes", "absences"];
export const DEFAULT_TOGGLES: Record<EventGroup, boolean> = { medication: true, notes: false, absences: false };

export interface TrajectoryEvent {
  id: string;
  kind: EventKind;
  group: EventGroup;
  /** yyyy-MM-dd */
  on: string;
  ms: number;
  drug?: string;
  dose?: string | null;
  noteId?: string;
}

export interface TrajectoryPoint {
  id: string;
  score: number;
  on: string;
  ms: number;
}

export interface InstrumentOption {
  instrumentId: string;
  name: string;
  code: string;
  count: number;
}

export function groupOf(kind: EventKind): EventGroup {
  if (kind === "note") return "notes";
  if (kind.startsWith("medication")) return "medication";
  return "absences";
}

/** Dash style per group (same neutral colour for all): solid, dotted, dashed. */
export function dashFor(kind: EventKind): string | undefined {
  if (kind === "note") return "2 3";
  if (groupOf(kind) === "absences") return "6 3";
  return undefined;
}

function inRange(ms: number, fromMs: number, toMs: number): boolean {
  return ms >= fromMs && ms < toMs + DAY_MS;
}

/** Points of one instrument within [fromMs, toMs], oldest first. */
export function pointsFor(scale: TimelineScale, fromMs: number, toMs: number): TrajectoryPoint[] {
  return scale.points
    .map((p) => ({ id: p.applicationId, score: p.score, on: p.appliedOn.slice(0, 10), ms: dayMs(p.appliedOn) }))
    .filter((p) => inRange(p.ms, fromMs, toMs))
    .sort((a, b) => a.ms - b.ms);
}

/** Instruments with the number of points in the period; most points first (ties by name). */
export function instrumentOptions(timeline: PatientTimeline, fromMs: number, toMs: number): InstrumentOption[] {
  return timeline.scales
    .map((s) => ({ instrumentId: s.instrumentId, name: s.name, code: s.code, count: pointsFor(s, fromMs, toMs).length }))
    .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name));
}

/** Default instrument: the one with most points in the period, or null when none has any. */
export function defaultInstrument(options: InstrumentOption[]): string | null {
  return options.length > 0 && options[0].count > 0 ? options[0].instrumentId : null;
}

const ABSENCE_KINDS: AttendanceKind[] = ["no_show", "cancelled_provider", "cancelled_patient"];

/**
 * Events in the period, oldest first, only for the enabled groups.
 * Medication: a treatment's first segment start is the start, each later segment start a dose change, and the
 * last segment's end (when set) the stop. Notes: signed sessions from the analysis. Absences: missed or
 * cancelled appointments.
 */
export function buildEvents(
  timeline: PatientTimeline | undefined,
  analysis: CaseAnalysis | undefined,
  toggles: Record<EventGroup, boolean>,
  fromMs: number,
  toMs: number,
): TrajectoryEvent[] {
  const out: TrajectoryEvent[] = [];
  const push = (e: Omit<TrajectoryEvent, "group" | "ms">) => {
    const ms = dayMs(e.on);
    const group = groupOf(e.kind);
    if (toggles[group] && inRange(ms, fromMs, toMs)) out.push({ ...e, group, ms });
  };

  if (timeline && toggles.medication) {
    for (const m of timeline.medications) {
      const segs = [...m.segments].sort((a, b) => dayMs(a.startedOn) - dayMs(b.startedOn));
      segs.forEach((s, i) =>
        push({
          id: `${s.medicationId}-${i === 0 ? "start" : "dose"}`,
          kind: i === 0 ? "medication_start" : "medication_dose",
          on: s.startedOn.slice(0, 10),
          drug: m.drug,
          dose: s.dose,
        }),
      );
      const last = segs[segs.length - 1];
      if (last?.endedOn) {
        push({ id: `${last.medicationId}-stop`, kind: "medication_stop", on: last.endedOn.slice(0, 10), drug: m.drug, dose: last.dose });
      }
    }
  }

  if (analysis && toggles.notes) {
    for (const s of analysis.sessions ?? []) push({ id: `note-${s.noteId}`, kind: "note", on: s.on.slice(0, 10), noteId: s.noteId });
  }

  if (timeline && toggles.absences) {
    for (const a of timeline.appointments) {
      if (ABSENCE_KINDS.includes(a.kind)) push({ id: `appt-${a.id}`, kind: a.kind as EventKind, on: a.on.slice(0, 10) });
    }
  }

  return out.sort((a, b) => a.ms - b.ms || a.id.localeCompare(b.id));
}

/** Axis: period start (or earliest point/event for "Tudo") to today (or the last date). */
export function axisDomain(fromIso: string | undefined, todayIsoValue: string, dates: number[]): { startMs: number; endMs: number } {
  let endMs = dayMs(todayIsoValue);
  let startMs = Infinity;
  for (const d of dates) {
    startMs = Math.min(startMs, d);
    endMs = Math.max(endMs, d);
  }
  if (fromIso) startMs = dayMs(fromIso);
  if (!Number.isFinite(startMs)) startMs = endMs - 30 * DAY_MS;
  return { startMs, endMs };
}
