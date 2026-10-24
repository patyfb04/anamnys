import type { ReactNode } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type { AdminThemeTerm } from "@anamnys/shared/api/adminThemes";

// 409 carries only a code; 400 carries the server's own Portuguese field messages.
export function describeError(error: unknown, t: TFunction): string[] {
  if (error instanceof ApiError) {
    if (error.code) {
      return [t(`admin.vocabulary.errors.${error.code}`, { defaultValue: t("admin.vocabulary.errors.generic") })];
    }
    if (error.errors) {
      return Object.values(error.errors).flat();
    }
    if (error.status === 403) return [t("admin.vocabulary.errors.forbidden")];
  }
  return [t("admin.vocabulary.errors.generic")];
}

export function ErrorNote({ error }: { error: unknown }) {
  const { t } = useTranslation();
  if (!error) return null;
  return (
    <div role="alert" className="text-body-sm text-error">
      {describeError(error, t).map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  );
}

export function CancelButton({ onClick }: { onClick: () => void }) {
  const { t } = useTranslation();
  return (
    <button
      type="button"
      onClick={onClick}
      className="rounded-radii-md px-4 py-2 text-label-lg text-primary hover:underline"
    >
      {t("common.cancel")}
    </button>
  );
}

export function SubmitButton({
  label,
  pending,
  disabled,
  onClick,
}: {
  label: string;
  pending: boolean;
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      disabled={pending || disabled}
      onClick={onClick}
      className="rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary hover:opacity-90 disabled:opacity-60"
    >
      {label}
    </button>
  );
}

export function Field({
  label,
  value,
  onChange,
  maxLength,
  hint,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  maxLength?: number;
  hint?: string;
}) {
  return (
    <label className="block">
      <span className="block text-label-sm text-onSurfaceVariant mb-1.5">{label}</span>
      <input
        value={value}
        maxLength={maxLength}
        onChange={(e) => onChange(e.target.value)}
        className="w-full rounded-radii-md border border-outlineVariant bg-surfaceContainerLow px-3.5 py-2.5 text-body-lg text-onSurface outline-none focus:border-primary"
      />
      {hint && <span className="block text-body-sm text-onSurfaceVariant mt-1">{hint}</span>}
    </label>
  );
}

export function Body({ children }: { children: ReactNode }) {
  return <div className="flex flex-col gap-4 p-5">{children}</div>;
}

export function termName(term: AdminThemeTerm, all: AdminThemeTerm[]): string {
  const parent = term.parentId ? all.find((x) => x.id === term.parentId) : undefined;
  return parent ? `${parent.label} / ${term.label}` : term.label;
}
