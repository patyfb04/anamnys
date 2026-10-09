// Calendar dates are "YYYY-MM-DD" strings on the practice's wall clock, never Date objects:
// the browser's own time zone must not move an appointment to another day or hour.
export const PRACTICE_ZONE = "America/Sao_Paulo";
export type CalendarView = "week" | "day";

function partsIn(instant: Date, zone: string) {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit",
    hour: "2-digit", minute: "2-digit", hourCycle: "h23",
  }).formatToParts(instant);
  const get = (type: string) => parts.find((p) => p.type === type)!.value;
  return { date: `${get("year")}-${get("month")}-${get("day")}`, hour: Number(get("hour")), minute: Number(get("minute")) };
}

export function zonedParts(instant: Date, zone: string): { date: string; minutes: number } {
  const p = partsIn(instant, zone);
  return { date: p.date, minutes: p.hour * 60 + p.minute };
}

export function todayIn(zone: string): string {
  return partsIn(new Date(), zone).date;
}

// Offset of `zone` from UTC at `instant`, in minutes (e.g. -180 for São Paulo).
function offsetMinutes(instant: Date, zone: string): number {
  const p = partsIn(instant, zone);
  const [y, m, d] = p.date.split("-").map(Number);
  const asUtc = Date.UTC(y, m - 1, d, p.hour, p.minute);
  return Math.round((asUtc - Math.floor(instant.getTime() / 60000) * 60000) / 60000);
}

export function zonedToInstant(date: string, minutes: number, zone: string): Date {
  const [y, m, d] = date.split("-").map(Number);
  const guess = Date.UTC(y, m - 1, d, 0, minutes);
  return new Date(guess - offsetMinutes(new Date(guess), zone) * 60000);
}

export function addDays(date: string, n: number): string {
  const [y, m, d] = date.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, d + n)).toISOString().slice(0, 10);
}

// Weeks start on Monday, as in the mockup.
export function weekStart(date: string): string {
  const [y, m, d] = date.split("-").map(Number);
  const weekday = new Date(Date.UTC(y, m - 1, d)).getUTCDay(); // 0 = Sunday
  return addDays(date, weekday === 0 ? -6 : 1 - weekday);
}

export function visibleDays(view: CalendarView, date: string): string[] {
  if (view === "day") return [date];
  const start = weekStart(date);
  return Array.from({ length: 7 }, (_, i) => addDays(start, i));
}

export function offsetLabel(zone: string, at: Date): string {
  const hours = offsetMinutes(at, zone) / 60;
  return `GMT${hours >= 0 ? "+" : ""}${hours}`;
}

export function formatTime(instant: Date, zone: string): string {
  return new Intl.DateTimeFormat("pt-BR", { hour: "2-digit", minute: "2-digit", timeZone: zone }).format(instant);
}

export function isWeekend(date: string): boolean {
  const [y, m, d] = date.split("-").map(Number);
  const weekday = new Date(Date.UTC(y, m - 1, d)).getUTCDay();
  return weekday === 0 || weekday === 6;
}

// "14:00" when the instant falls on the reference instant's day in the zone, otherwise
// "qui., 8 de out. 14:00".
export function formatTimeWithDate(instant: Date, reference: Date, zone: string): string {
  const dayKey = (d: Date) => new Intl.DateTimeFormat("en-CA", { timeZone: zone }).format(d);
  if (dayKey(instant) === dayKey(reference)) return formatTime(instant, zone);
  const date = new Intl.DateTimeFormat("pt-BR", { weekday: "short", day: "numeric", month: "short", timeZone: zone }).format(instant);
  return `${date} ${formatTime(instant, zone)}`;
}
