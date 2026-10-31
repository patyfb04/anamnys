// dd/mm/aaaa from an ISO day (no time zone shift).
export function formatShortDate(iso: string): string {
  const [y, m, d] = iso.slice(0, 10).split("-");
  return `${d}/${m}/${y}`;
}

// dd/mm/aaaa of an instant in the browser's time zone.
export function formatInstant(iso: string): string {
  return new Date(iso).toLocaleDateString("pt-BR");
}

// Local calendar days from an instant until today (never negative).
export function daysBetweenNow(iso: string): number {
  const day = (d: Date) => Date.UTC(d.getFullYear(), d.getMonth(), d.getDate());
  return Math.max(0, Math.round((day(new Date()) - day(new Date(iso))) / 86_400_000));
}
