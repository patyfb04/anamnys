import { useId, type InputHTMLAttributes } from "react";

interface Props extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  error?: string[];
}

export const inputClass =
  "w-full bg-surfaceContainerLowest border border-outlineVariant focus:border-primary rounded-radii-md px-3 py-2 text-body-md text-onSurface outline-none aria-[invalid=true]:border-error";

// Labeled input with its validation message underneath. The message sits outside the
// <label> and is linked by aria-describedby, so it is announced without becoming part of
// the field's name.
export default function FormField({ label, error, className, ...input }: Props) {
  const errorId = useId();
  return (
    <div className={`flex flex-col gap-1 ${className ?? ""}`}>
      <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
        {label}
        <input
          className={inputClass}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          {...input}
        />
      </label>
      {error && (
        <span id={errorId} className="text-label-md text-error">
          {error[0]}
        </span>
      )}
    </div>
  );
}
