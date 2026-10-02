import type { InputHTMLAttributes } from "react";

interface Props extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  error?: string[];
}

export const inputClass =
  "w-full bg-surfaceContainerLowest border border-outlineVariant focus:border-primary rounded-radii-md px-3 py-2 text-body-md text-onSurface outline-none aria-[invalid=true]:border-error";

// Labeled input with its server-side (or client-side) validation message underneath.
export default function FormField({ label, error, className, ...input }: Props) {
  return (
    <label className={`flex flex-col gap-1 text-label-md text-onSurfaceVariant ${className ?? ""}`}>
      {label}
      <input className={inputClass} aria-invalid={error ? true : undefined} {...input} />
      {error && <span className="text-label-md text-error">{error[0]}</span>}
    </label>
  );
}
