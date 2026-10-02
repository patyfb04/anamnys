// Appointments are booked in the practice's zone (Appointments.Timezone defaults to
// America/Sao_Paulo), so dates and times render there regardless of the browser's zone.
const PRACTICE_TIME_ZONE = "America/Sao_Paulo";

export function formatDate(iso: string | null, locale: string): string {
  if (!iso) return "—";
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: PRACTICE_TIME_ZONE }).format(new Date(iso));
}

export function formatTime(iso: string | null, locale: string): string {
  if (!iso) return "";
  return new Intl.DateTimeFormat(locale, { timeStyle: "short", timeZone: PRACTICE_TIME_ZONE }).format(new Date(iso));
}

// Today in the practice's zone as "YYYY-MM-DD" (en-CA formats dates that way), for
// date-input defaults and max attributes.
export function todayIso(): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: PRACTICE_TIME_ZONE }).format(new Date());
}

// "YYYY-MM-DD" date-only values render as calendar dates, with no time-zone shift.
export function formatDay(day: string | null, locale: string): string {
  if (!day) return "—";
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: "UTC" }).format(new Date(`${day}T00:00:00Z`));
}

export function ageFrom(day: string | null): number | null {
  if (!day) return null;
  const [y, m, d] = day.split("-").map(Number);
  const [ty, tm, td] = todayIso().split("-").map(Number);
  return ty - y - (tm < m || (tm === m && td < d) ? 1 : 0);
}
