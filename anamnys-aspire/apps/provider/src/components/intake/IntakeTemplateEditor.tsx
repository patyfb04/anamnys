import { useState, type ReactNode } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ArrowDown, ArrowUp, Loader2, Plus, Trash2 } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import { intakeApi } from "@anamnys/shared/api/intake";
import {
  INTAKE_ANSWER_TYPES,
  INTAKE_TARGET_FIELDS,
  type IntakeAnswerType,
  type IntakeTargetField,
  type IntakeTemplateDetail,
  type IntakeTemplateQuestionInput,
} from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import Switch from "@/components/settings/Switch";
import { inputClass } from "@/components/patients/FormField";
import { answerTypeFor, errorCode, targetLabelKey } from "./intakeModel";

interface EditorQuestion {
  uid: number;
  key: string;
  sectionKey: string;
  sectionTitle: string;
  label: string;
  helpText: string;
  answerType: IntakeAnswerType;
  choicesText: string;
  required: boolean;
  targetField: IntakeTargetField | null;
  disabled: boolean;
}

let nextUid = 1;

function fromTemplate(template: IntakeTemplateDetail): EditorQuestion[] {
  return template.sections.flatMap((s) =>
    s.questions.map((q) => ({
      uid: nextUid++,
      key: q.key,
      sectionKey: s.key,
      sectionTitle: s.title,
      label: q.label,
      helpText: q.helpText ?? "",
      answerType: q.answerType,
      choicesText: (q.choices ?? []).join("\n"),
      required: q.required,
      targetField: q.targetField,
      disabled: q.disabled,
    })),
  );
}

// Sections in the order of their first question; questions keep their order inside each section.
function groups(questions: EditorQuestion[]) {
  const order: string[] = [];
  const bySection = new Map<string, EditorQuestion[]>();
  for (const q of questions) {
    if (!bySection.has(q.sectionKey)) {
      order.push(q.sectionKey);
      bySection.set(q.sectionKey, []);
    }
    bySection.get(q.sectionKey)!.push(q);
  }
  return order.map((key) => ({ key, title: bySection.get(key)![0].sectionTitle, questions: bySection.get(key)! }));
}

const flatten = (gs: ReturnType<typeof groups>) => gs.flatMap((g) => g.questions);

function uniqueKey(prefix: string, taken: Set<string>) {
  let n = 1;
  while (taken.has(`${prefix}_${n}`)) n++;
  return `${prefix}_${n}`;
}

function toInput(q: EditorQuestion): IntakeTemplateQuestionInput {
  const choices = q.answerType === "choice" ? q.choicesText.split("\n").map((c) => c.trim()).filter(Boolean) : null;
  return {
    key: q.key,
    sectionKey: q.sectionKey,
    sectionTitle: q.sectionTitle.trim(),
    label: q.label.trim(),
    helpText: q.helpText.trim() || null,
    answerType: q.answerType,
    choices,
    required: q.required,
    targetField: q.targetField,
    disabled: q.disabled,
  };
}

// Edits an own intake template: questions on/off, added, reordered, label, help, type, choices, required and record
// destination. Saving a template already used by intakes creates a new version (the server answers with it).
export default function IntakeTemplateEditor({ template, onClose }: { template: IntakeTemplateDetail; onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [name, setName] = useState(template.name);
  const [questions, setQuestions] = useState<EditorQuestion[]>(() => fromTemplate(template));
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  const gs = groups(questions);
  const flat = flatten(gs);
  const indexOf = new Map(flat.map((q, i) => [q.uid, i]));
  const fieldError = (q: EditorQuestion, field: string) => errors[`questions[${indexOf.get(q.uid)}].${field}`]?.[0];

  const update = (uid: number, patch: Partial<EditorQuestion>) =>
    setQuestions((qs) => qs.map((q) => (q.uid === uid ? { ...q, ...patch } : q)));

  const setSectionTitle = (sectionKey: string, title: string) =>
    setQuestions((qs) => qs.map((q) => (q.sectionKey === sectionKey ? { ...q, sectionTitle: title } : q)));

  const moveSection = (index: number, delta: number) => {
    const next = [...gs];
    const [g] = next.splice(index, 1);
    next.splice(index + delta, 0, g);
    setQuestions(flatten(next));
  };

  const moveQuestion = (sectionIndex: number, index: number, delta: number) => {
    const next = gs.map((g) => ({ ...g, questions: [...g.questions] }));
    const list = next[sectionIndex].questions;
    const [q] = list.splice(index, 1);
    list.splice(index + delta, 0, q);
    setQuestions(flatten(next));
  };

  const newQuestion = (sectionKey: string, sectionTitle: string): EditorQuestion => ({
    uid: nextUid++,
    key: uniqueKey("pergunta", new Set(questions.map((q) => q.key))),
    sectionKey,
    sectionTitle,
    label: "",
    helpText: "",
    answerType: "text",
    choicesText: "",
    required: false,
    targetField: null,
    disabled: false,
  });

  const addQuestion = (sectionIndex: number) => {
    const next = gs.map((g) => ({ ...g, questions: [...g.questions] }));
    const g = next[sectionIndex];
    g.questions.push(newQuestion(g.key, g.title));
    setQuestions(flatten(next));
  };

  const addSection = () => {
    const key = uniqueKey("secao", new Set(questions.map((q) => q.sectionKey)));
    setQuestions((qs) => [...qs, newQuestion(key, t("intake.settings.editor.newSection"))]);
  };

  const remove = (uid: number) => setQuestions((qs) => qs.filter((q) => q.uid !== uid));

  const save = useMutation({
    mutationFn: () => intakeApi.updateTemplate(template.id, { name: name.trim(), questions: flat.map(toInput) }),
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: ["intake-templates"] });
      queryClient.setQueryData(["intake-template", saved.id], saved);
      toast.success(
        saved.id !== template.id
          ? t("intake.settings.editor.savedNewVersion", { version: saved.version })
          : t("intake.settings.editor.saved"),
      );
      onClose();
    },
    onError: (e) => {
      const code = errorCode(e);
      if (e instanceof ApiError && e.status === 400 && e.errors) {
        setErrors(e.errors);
        toast.error(t("intake.settings.editor.saveFailed"));
      } else if (code === "name_taken") {
        setErrors({ name: [t("intake.settings.nameTaken")] });
      } else if (code === "template_read_only") toast.error(t("intake.settings.editor.readOnly"));
      else if (code === "template_archived") toast.error(t("intake.settings.editor.archivedTemplate"));
      else toast.error(t("intake.settings.editor.saveFailed"));
    },
  });

  const smallButton =
    "inline-flex items-center justify-center rounded-radii-md p-1.5 text-onSurfaceVariant hover:bg-pastelLavender disabled:opacity-30 disabled:hover:bg-transparent";

  return (
    <Modal
      title={t("intake.settings.editor.title")}
      onClose={onClose}
      footer={
        <>
          <button
            type="button"
            onClick={onClose}
            className="rounded-radii-md border border-outlineVariant px-4 py-2 text-label-lg text-onSurface hover:bg-pastelLavender"
          >
            {t("intake.settings.editor.cancel")}
          </button>
          <button
            type="button"
            disabled={save.isPending}
            onClick={() => {
              // A choice question needs at least one choice; caught here before the server's 400.
              const local: Record<string, string[]> = {};
              flat.forEach((q, i) => {
                if (q.answerType === "choice" && !q.choicesText.split("\n").some((c) => c.trim())) {
                  local[`questions[${i}].choices`] = [t("intake.settings.editor.choicesRequired")];
                }
              });
              setErrors(local);
              if (Object.keys(local).length > 0) {
                toast.error(t("intake.settings.editor.saveFailed"));
                return;
              }
              save.mutate();
            }}
            className="inline-flex items-center gap-2 rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-60"
          >
            {save.isPending && <Loader2 size={16} className="animate-spin" aria-hidden />}
            {t("intake.settings.editor.save")}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-5 p-5">
        <label className="flex flex-col gap-1">
          <span className="text-label-lg text-onSurface">{t("intake.settings.editor.name")}</span>
          <input
            className={inputClass}
            value={name}
            maxLength={100}
            aria-invalid={errors.name ? true : undefined}
            onChange={(e) => setName(e.target.value)}
          />
          {errors.name && <span role="alert" className="text-body-sm text-error">{errors.name[0]}</span>}
        </label>
        {template.inUse && <p className="rounded-radii-lg bg-surfaceContainerLow p-3 text-body-sm text-onSurfaceVariant">{t("intake.settings.editor.inUseHint")}</p>}
        {errors.questions && <p role="alert" className="text-body-sm text-error">{errors.questions[0]}</p>}

        {gs.map((g, si) => (
          <section key={g.key} className="flex flex-col gap-3 rounded-radii-lg border border-outlineVariant p-4">
            <div className="flex items-end gap-2">
              <label className="flex flex-1 flex-col gap-1">
                <span className="text-label-md text-onSurfaceVariant">{t("intake.settings.editor.sectionTitle")}</span>
                <input
                  className={inputClass}
                  value={g.title}
                  maxLength={100}
                  aria-invalid={g.questions.some((q) => fieldError(q, "sectionTitle")) ? true : undefined}
                  onChange={(e) => setSectionTitle(g.key, e.target.value)}
                />
              </label>
              <button type="button" className={smallButton} disabled={si === 0} onClick={() => moveSection(si, -1)} title={t("intake.settings.editor.moveSectionUp")} aria-label={t("intake.settings.editor.moveSectionUp")}>
                <ArrowUp size={16} />
              </button>
              <button type="button" className={smallButton} disabled={si === gs.length - 1} onClick={() => moveSection(si, 1)} title={t("intake.settings.editor.moveSectionDown")} aria-label={t("intake.settings.editor.moveSectionDown")}>
                <ArrowDown size={16} />
              </button>
            </div>
            {g.questions.map((q) => fieldError(q, "sectionTitle")).find(Boolean) && (
              <span role="alert" className="text-body-sm text-error">
                {g.questions.map((q) => fieldError(q, "sectionTitle")).find(Boolean)}
              </span>
            )}

            <ol className="flex flex-col gap-3">
              {g.questions.map((q, qi) => (
                <li key={q.uid} className={`flex flex-col gap-3 rounded-radii-md p-3 ${q.disabled ? "bg-surfaceContainerHighest/60" : "bg-surfaceContainerLow"}`}>
                  <div className="flex items-center gap-2">
                    <Switch
                      checked={!q.disabled}
                      onChange={(on) => update(q.uid, { disabled: !on })}
                      label={t("intake.settings.editor.enabled")}
                    />
                    <span className="text-label-md text-onSurfaceVariant">{t("intake.settings.editor.enabled")}</span>
                    <div className="ml-auto flex items-center gap-1">
                      <button type="button" className={smallButton} disabled={qi === 0} onClick={() => moveQuestion(si, qi, -1)} title={t("intake.settings.editor.moveUp")} aria-label={t("intake.settings.editor.moveUp")}>
                        <ArrowUp size={16} />
                      </button>
                      <button type="button" className={smallButton} disabled={qi === g.questions.length - 1} onClick={() => moveQuestion(si, qi, 1)} title={t("intake.settings.editor.moveDown")} aria-label={t("intake.settings.editor.moveDown")}>
                        <ArrowDown size={16} />
                      </button>
                      <button type="button" className={`${smallButton} hover:text-error`} onClick={() => remove(q.uid)} title={t("intake.settings.editor.remove")} aria-label={t("intake.settings.editor.remove")}>
                        <Trash2 size={16} />
                      </button>
                    </div>
                  </div>
                  <Field label={t("intake.settings.editor.label")} error={fieldError(q, "label") ?? fieldError(q, "key")}>
                    <input className={inputClass} value={q.label} maxLength={300} onChange={(e) => update(q.uid, { label: e.target.value })} />
                  </Field>
                  <Field label={t("intake.settings.editor.help")} error={fieldError(q, "helpText")}>
                    <input className={inputClass} value={q.helpText} maxLength={500} onChange={(e) => update(q.uid, { helpText: e.target.value })} />
                  </Field>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Field label={t("intake.settings.editor.type")} error={fieldError(q, "answerType")}>
                      <select
                        className={inputClass}
                        value={q.answerType}
                        onChange={(e) => {
                          const answerType = e.target.value as IntakeAnswerType;
                          // A destination that needs another type is dropped with the change.
                          const keep = q.targetField && answerTypeFor(q.targetField) === answerType;
                          update(q.uid, { answerType, targetField: keep ? q.targetField : null });
                        }}
                      >
                        {INTAKE_ANSWER_TYPES.map((type) => (
                          <option key={type} value={type}>
                            {t(`intake.types.${type}`)}
                          </option>
                        ))}
                      </select>
                    </Field>
                    <Field label={t("intake.settings.editor.destination")} error={fieldError(q, "targetField")}>
                      <select
                        className={inputClass}
                        value={q.targetField ?? ""}
                        onChange={(e) => {
                          const target = (e.target.value || null) as IntakeTargetField | null;
                          update(q.uid, target ? { targetField: target, answerType: answerTypeFor(target) } : { targetField: null });
                        }}
                      >
                        <option value="">{t("intake.settings.editor.noDestination")}</option>
                        {INTAKE_TARGET_FIELDS.map((f) => (
                          <option key={f} value={f}>
                            {t(targetLabelKey(f))}
                          </option>
                        ))}
                      </select>
                      {q.targetField && (
                        <span className="text-body-sm text-onSurfaceVariant">
                          {t("intake.settings.editor.destinationTypeHint", { type: t(`intake.types.${answerTypeFor(q.targetField)}`) })}
                        </span>
                      )}
                    </Field>
                  </div>
                  {q.answerType === "choice" && (
                    <Field label={t("intake.settings.editor.choices")} error={fieldError(q, "choices")}>
                      <textarea
                        className={`${inputClass} min-h-24`}
                        value={q.choicesText}
                        onChange={(e) => update(q.uid, { choicesText: e.target.value })}
                      />
                    </Field>
                  )}
                  <label className="inline-flex items-center gap-2 self-start text-label-md text-onSurface">
                    <input type="checkbox" checked={q.required} onChange={(e) => update(q.uid, { required: e.target.checked })} />
                    {t("intake.settings.editor.required")}
                  </label>
                </li>
              ))}
            </ol>
            <button
              type="button"
              onClick={() => addQuestion(si)}
              className="inline-flex items-center gap-1 self-start rounded-radii-md px-2 py-1 text-label-md text-primary hover:bg-pastelLavender"
            >
              <Plus size={14} aria-hidden />
              {t("intake.settings.editor.addQuestion")}
            </button>
          </section>
        ))}
        <button
          type="button"
          onClick={addSection}
          className="inline-flex items-center gap-1 self-start rounded-radii-md border border-outlineVariant px-3 py-1.5 text-label-md text-primary hover:bg-pastelLavender"
        >
          <Plus size={14} aria-hidden />
          {t("intake.settings.editor.addSection")}
        </button>
      </div>
    </Modal>
  );
}

function Field({ label, error, children }: { label: string; error?: string; children: ReactNode }) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-label-md text-onSurfaceVariant">{label}</span>
      {children}
      {error && (
        <span role="alert" className="text-body-sm text-error">
          {error}
        </span>
      )}
    </label>
  );
}
