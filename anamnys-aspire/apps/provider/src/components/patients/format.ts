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
