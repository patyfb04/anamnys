import type { AppointmentStatus } from "@anamnys/shared/lib/types";

// Card and chip colors per status (spec §4).
export const STATUS_STYLE: Record<AppointmentStatus, string> = {
  scheduled: "bg-primaryContainer/15 border-l-primary text-onSurface",
  confirmed: "bg-secondaryContainer border-l-secondary text-onSecondaryContainer",
  attended: "bg-surfaceContainer border-l-outline text-onSurfaceVariant",
  no_show: "bg-errorContainer border-l-error text-onErrorContainer",
  cancelled: "bg-surfaceContainerLow border-l-outlineVariant text-onSurfaceVariant line-through opacity-70",
};

// Mirrors AppointmentTransitions on the server; the server stays the authority.
export const NEXT_STATUSES: Record<AppointmentStatus, AppointmentStatus[]> = {
  scheduled: ["confirmed", "attended", "no_show", "cancelled"],
  confirmed: ["scheduled", "attended", "no_show", "cancelled"],
  attended: ["scheduled"],
  no_show: ["scheduled"],
  cancelled: [],
};

const ALL: AppointmentStatus[] = ["scheduled", "confirmed", "attended", "cancelled", "no_show"];
export const ALL_STATUSES = ALL;

export function isStatus(value: unknown): value is AppointmentStatus {
  return typeof value === "string" && (ALL as string[]).includes(value);
}
