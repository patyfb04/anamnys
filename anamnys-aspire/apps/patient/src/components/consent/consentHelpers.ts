import { ApiError } from "@anamnys/shared/api/client";

export const fieldClass =
  "w-full bg-surfaceContainerLowest border border-outlineVariant focus:border-primary rounded-radii-md px-3 py-2.5 text-body-md text-onSurface outline-none";

// Every dead, unknown, used or expired link answers 410 (the server says the same for all of them); 404 reads the same.
export const isDeadLink = (e: unknown) => e instanceof ApiError && (e.status === 410 || e.status === 404);

// The machine-readable code of a failed submit, when the server sent one.
export const failureCode = (e: unknown): string => (e instanceof ApiError && e.code ? e.code : "unknown");
