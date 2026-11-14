import { useId } from "react";
import { useTranslation } from "react-i18next";
import type { IntakeQuestion } from "@anamnys/shared/lib/types";
import { inputClass } from "@/components/patients/FormField";
import { formatDay, todayIso } from "@/components/patients/format";

interface Props {
  question: IntakeQuestion;
  value: string;
  readOnly: boolean;
  invalid: boolean;
  missing: boolean;
  onChange: (value: string) => void;
  // Text, number and date fields save on blur; choices and yes/no save as soon as they change.
  onCommit: (value: string) => void;
}

// One intake question with the input its answer type needs (text, number, date, yes/no, choice).
export default function IntakeQuestionField({ question: q, value, readOnly, invalid, missing, onChange, onCommit }: Props) {
  const { t, i18n } = useTranslation();
  const id = useId();
  const helpId = `${id}-help`;
  const errorId = `${id}-error`;
  const describedBy = [q.helpText ? helpId : null, invalid || missing ? errorId : null].filter(Boolean).join(" ") || undefined;
  const ariaInvalid = invalid || missing ? true : undefined;

  const label = (
    <span className="text-label-lg text-onSurface">
      {q.label}
      {q.required && (
        <>
          <span aria-hidden className="ml-0.5 text-error">
            *
          </span>
          <span className="sr-only"> ({t("intake.page.required")})</span>
        </>
      )}
    </span>
  );

  const help = q.helpText && (
    <span id={helpId} className="text-body-sm text-onSurfaceVariant">
      {q.helpText}
    </span>
  );

  const error = (invalid || missing) && (
    <span id={errorId} role="alert" className="text-body-sm text-error">
      {invalid ? t("intake.page.invalidAnswer") : t("intake.page.requiredField")}
    </span>
  );

  if (readOnly) {
    const shown = !value
      ? t("intake.page.noAnswer")
      : q.answerType === "boolean"
        ? value === "true"
          ? t("intake.page.yes")
          : t("intake.page.no")
        : q.answerType === "date"
          ? formatDay(value, i18n.language)
          : value;
    return (
      <div className="flex flex-col gap-1">
        {label}
        <p className={`whitespace-pre-wrap break-words text-body-md ${value ? "text-onSurface" : "italic text-onSurfaceVariant"}`}>
          {shown}
        </p>
      </div>
    );
  }

  if (q.answerType === "boolean") {
    const option = (v: string, text: string) => (
      <button
        type="button"
        role="radio"
        aria-checked={value === v}
        onClick={() => {
          // A second click on the chosen option clears the answer.
          const next = value === v ? "" : v;
          onChange(next);
          onCommit(next);
        }}
        className={`rounded-radii-full border px-4 py-1.5 text-label-lg ${
          value === v ? "border-transparent bg-primary text-onPrimary" : "border-outlineVariant text-onSurface hover:bg-pastelLavender"
        }`}
      >
        {text}
      </button>
    );
    return (
      <div className="flex flex-col gap-1">
        <span id={`${id}-label`}>{label}</span>
        {help}
        <div role="radiogroup" aria-labelledby={`${id}-label`} aria-describedby={describedBy} aria-invalid={ariaInvalid} className="flex gap-2">
          {option("true", t("intake.page.yes"))}
          {option("false", t("intake.page.no"))}
        </div>
        {error}
      </div>
    );
  }

  let input;
  if (q.answerType === "choice") {
    input = (
      <select
        id={id}
        className={inputClass}
        value={value}
        aria-invalid={ariaInvalid}
        aria-describedby={describedBy}
        onChange={(e) => {
          onChange(e.target.value);
          onCommit(e.target.value);
        }}
      >
        <option value="">{t("intake.page.choose")}</option>
        {(q.choices ?? []).map((c) => (
          <option key={c} value={c}>
            {c}
          </option>
        ))}
        {/* An answer no longer among the choices stays visible until changed. */}
        {value && !(q.choices ?? []).includes(value) && <option value={value}>{value}</option>}
      </select>
    );
  } else if (q.answerType === "text" && !q.targetField?.startsWith("patient.")) {
    input = (
      <textarea
        id={id}
        className={`${inputClass} min-h-20`}
        rows={2}
        // A medication, diagnosis or objective answer must fit the record item (server limit 500).
        maxLength={q.targetField ? 500 : 4000}
        value={value}
        aria-invalid={ariaInvalid}
        aria-describedby={describedBy}
        onChange={(e) => onChange(e.target.value)}
        onBlur={(e) => onCommit(e.target.value)}
      />
    );
  } else {
    const type = q.answerType === "date" ? "date" : q.targetField === "patient.contactEmail" ? "email" : q.targetField === "patient.phone" ? "tel" : "text";
    input = (
      <input
        id={id}
        type={type}
        inputMode={q.answerType === "number" ? "decimal" : undefined}
        className={`${inputClass} ${q.answerType === "text" ? "" : "max-w-xs"}`}
        min={q.answerType === "date" && q.targetField === "patient.dateOfBirth" ? "1900-01-01" : undefined}
        max={q.answerType === "date" && q.targetField === "patient.dateOfBirth" ? todayIso() : undefined}
        maxLength={q.answerType === "text" ? (q.targetField === "patient.phone" ? 40 : 4000) : undefined}
        value={value}
        aria-invalid={ariaInvalid}
        aria-describedby={describedBy}
        onChange={(e) => {
          // A date typed only in part reads as "": keep the saved answer until the date is complete or cleared.
          if (!e.target.validity.badInput) onChange(e.target.value);
        }}
        onBlur={(e) => {
          if (!e.target.validity.badInput) onCommit(e.target.value);
        }}
      />
    );
  }

  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id}>{label}</label>
      {help}
      {input}
      {error}
    </div>
  );
}
