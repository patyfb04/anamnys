import type { TFunction } from "i18next";
import type { BlockItem, BlockKind, CalendarOverlay } from "@anamnys/shared/lib/types";
import { PRACTICE_ZONE, addDays, zonedParts, zonedToInstant } from "./calendarTime";

const DAY_MINUTES = 24 * 60;

export interface Segment {
  start: number; // minutes since local midnight
  end: number;
}

export interface DayBand extends Segment {
  block: BlockItem;
}

export interface DayOverlay {
  windows: Segment[];
  holidayName: string | null;
  bands: DayBand[];
  // Whether the overlay holds this date (false while loading or after an error).
  known: boolean;
}

// Band colors per block kind; holidays use the vacation tone's sibling so the two read apart.
export const BAND_STYLE: Record<BlockKind, string> = {
  vacation: "bg-pastelTeal/70 border-l-4 border-l-secondary text-onPastelTeal",
  block: "bg-pastelPink/70 border-l-4 border-l-error text-onPastelPink",
  holiday: "bg-pastelBlue/70 border-l-4 border-l-primary text-onPastelBlue",
};
export const HOLIDAY_BAND_STYLE = "bg-pastelBlue/60 border-l-4 border-l-primary text-onPastelBlue";

// "Férias", "Bloqueio: congresso", "Feriado local: aniversário da cidade".
export function blockLabel(t: TFunction, block: BlockItem): string {
  const kind = t(`blocks.kind.${block.kind}`);
  return block.reason ? `${kind}: ${block.reason}` : kind;
}

// Minutes of an instant on `date`, clamped to [0, 1440].
function minutesOn(instant: Date, date: string): number {
  const p = zonedParts(instant, PRACTICE_ZONE);
  if (p.date < date) return 0;
  if (p.date > date) return DAY_MINUTES;
  return p.minutes;
}

// What one local date shows: working windows, the applicable national holiday, and the blocks cut to the day.
export function overlayFor(date: string, overlay: CalendarOverlay | undefined): DayOverlay {
  const day = overlay?.days.find((d) => d.date === date);
  const dayStart = zonedToInstant(date, 0, PRACTICE_ZONE).getTime();
  const dayEnd = zonedToInstant(addDays(date, 1), 0, PRACTICE_ZONE).getTime();
  const windows = (day?.windows ?? []).map((w) => ({
    start: minutesOn(new Date(w.startsAt), date),
    end: minutesOn(new Date(w.endsAt), date),
  }));
  const bands = (overlay?.blocks ?? [])
    .filter((b) => new Date(b.startsAt).getTime() < dayEnd && new Date(b.endsAt).getTime() > dayStart)
    .map((block) => ({
      block,
      start: minutesOn(new Date(Math.max(new Date(block.startsAt).getTime(), dayStart)), date),
      end: new Date(block.endsAt).getTime() >= dayEnd ? DAY_MINUTES : minutesOn(new Date(block.endsAt), date),
    }))
    .sort((a, b) => a.start - b.start);
  return { windows, holidayName: day?.holiday?.name ?? null, bands, known: day !== undefined };
}

// The parts of [from, to] not covered by the windows (the hours to shade).
export function outsideSegments(windows: Segment[], from: number, to: number): Segment[] {
  const sorted = [...windows].sort((a, b) => a.start - b.start);
  const result: Segment[] = [];
  let cursor = from;
  for (const w of sorted) {
    if (w.start > cursor) result.push({ start: cursor, end: Math.min(w.start, to) });
    cursor = Math.max(cursor, w.end);
    if (cursor >= to) break;
  }
  if (cursor < to) result.push({ start: cursor, end: to });
  return result.filter((s) => s.end > s.start);
}
