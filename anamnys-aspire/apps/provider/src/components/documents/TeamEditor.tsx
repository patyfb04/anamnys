import { useTranslation } from "react-i18next";
import type { DocumentTeamMember } from "@anamnys/shared/lib/types";

const MAX_MEMBERS = 10;

const inputClass =
  "w-full bg-surfaceContainerLowest border border-outlineVariant focus:border-primary rounded-radii-md px-3 py-2 text-body-md text-onSurface outline-none read-only:bg-surfaceContainerLow";
const textareaClass = `${inputClass} min-h-24 resize-y`;

const EMPTY: DocumentTeamMember = { name: "", category: "", registry: "", analysis: "" };

interface Props {
  members: DocumentTeamMember[];
  onChange: (members: DocumentTeamMember[]) => void;
  readOnly: boolean;
  errors?: Record<string, string>;
}

export default function TeamEditor({ members, onChange, readOnly, errors = {} }: Props) {
  const { t } = useTranslation();
  const update = (i: number, patch: Partial<DocumentTeamMember>) =>
    onChange(members.map((m, idx) => (idx === i ? { ...m, ...patch } : m)));
  const atLimit = members.length >= MAX_MEMBERS;

  return (
    <div className="flex flex-col gap-4">
      {errors.team && <p className="text-label-md text-error">{errors.team}</p>}
      {members.map((m, i) => (
        <div key={i} className="flex flex-col gap-3 rounded-radii-lg border border-outlineVariant p-4">
          {errors[`team[${i}]`] && <p className="text-label-md text-error">{errors[`team[${i}]`]}</p>}
          <div className="flex items-center justify-between">
            <span className="text-label-lg text-onSurface">{t("documents.team.member", { n: i + 1 })}</span>
            {!readOnly && (
              <button
                type="button"
                onClick={() => onChange(members.filter((_, idx) => idx !== i))}
                className="text-label-lg text-error hover:underline"
              >
                {t("documents.team.remove")}
              </button>
            )}
          </div>
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("documents.team.name")} error={errors[`team[${i}].name`]}>
              <input
                className={inputClass}
                value={m.name}
                maxLength={200}
                readOnly={readOnly}
                onChange={(e) => update(i, { name: e.target.value })}
              />
            </Field>
            <Field label={t("documents.team.category")} error={errors[`team[${i}].category`]}>
              <input
                className={inputClass}
                value={m.category}
                maxLength={100}
                readOnly={readOnly}
                placeholder={t("documents.team.categoryPlaceholder")}
                onChange={(e) => update(i, { category: e.target.value })}
              />
            </Field>
            <Field label={t("documents.team.registry")} error={errors[`team[${i}].registry`]}>
              <input
                className={inputClass}
                value={m.registry}
                maxLength={50}
                readOnly={readOnly}
                placeholder={t("documents.team.registryPlaceholder")}
                onChange={(e) => update(i, { registry: e.target.value })}
              />
            </Field>
          </div>
          <Field label={t("documents.team.analysis")} error={errors[`team[${i}].analysis`]}>
            <textarea
              className={textareaClass}
              value={m.analysis}
              maxLength={20000}
              readOnly={readOnly}
              onChange={(e) => update(i, { analysis: e.target.value })}
            />
          </Field>
        </div>
      ))}
      {!readOnly && (
        <div className="flex items-center gap-3">
          <button
            type="button"
            disabled={atLimit}
            onClick={() => onChange([...members, { ...EMPTY }])}
            className="self-start text-label-lg text-primary hover:underline disabled:opacity-60 disabled:no-underline"
          >
            {t("documents.team.add")}
          </button>
          {atLimit && <span className="text-body-sm text-onSurfaceVariant">{t("documents.team.limit")}</span>}
        </div>
      )}
    </div>
  );
}

function Field({ label, error, children }: { label: string; error?: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
      <span>{label}</span>
      {children}
      {error && <span className="text-label-md text-error">{error}</span>}
    </label>
  );
}
