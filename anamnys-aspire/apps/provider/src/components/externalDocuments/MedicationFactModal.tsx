import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { medicationTimelineApi } from "@anamnys/shared/api/medicationTimeline";
import type {
  DocumentFact,
  MedicationDraft,
  MedicationFactAction,
} from "@anamnys/shared/lib/types";
import FormField, { inputClass } from "@/components/patients/FormField";
import Modal from "@/components/patients/Modal";
import { formatDate } from "@/components/patients/format";

const SERVER_FIELDS = [
  "drug",
  "dose",
  "posology",
  "startedOn",
  "endedOn",
  "targetMedicationId",
  "action",
] as const;
const CONFLICT_CODES = [
  "archived",
  "not_pending",
  "not_medication",
  "already_recorded",
  "target_not_active",
];

// The server keys validation errors by field name (any casing).
function fieldErrors(e: unknown): Record<string, string[]> {
  const out: Record<string, string[]> = {};
  if (e instanceof ApiError && e.errors) {
    for (const [k, v] of Object.entries(e.errors)) {
      const key = SERVER_FIELDS.find(
        (f) => f.toLowerCase() === k.toLowerCase(),
      );
      if (key) out[key] = v;
    }
  }
  return out;
}

// Confirming or correcting a medication fact: a short form prefilled from the document (a suggestion
// only) that saves a medication record linked to the fact. With an active record of the same drug the
// provider chooses between a dose change, a suspension and a new record.
export default function MedicationFactModal({
  documentId,
  fact,
  onSaved,
  onClose,
}: {
  documentId: string;
  fact: DocumentFact;
  onSaved: () => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const query = useQuery({
    queryKey: ["medication-draft", documentId, fact.id],
    queryFn: () => medicationTimelineApi.draft(documentId, fact.id),
    retry: false,
    gcTime: 0,
  });
  const draft = query.data;
  const loadError =
    query.error instanceof ApiError &&
    query.error.code &&
    CONFLICT_CODES.includes(query.error.code)
      ? t(`documentFacts.medication.errors.${query.error.code}`)
      : t("documentFacts.medication.loadFailed");

  return (
    <Modal title={t("documentFacts.medication.title")} onClose={onClose}>
      {query.isPending ? (
        <p className="p-5 text-body-md text-onSurfaceVariant">
          {t("documentFacts.medication.loading")}
        </p>
      ) : query.isError || !draft ? (
        <p className="p-5 text-body-md text-error">{loadError}</p>
      ) : draft.recorded ? (
        <p className="p-5 text-body-md text-onSurfaceVariant">
          {t("documentFacts.medication.errors.already_recorded")}
        </p>
      ) : (
        <MedicationForm
          documentId={documentId}
          draft={draft}
          onSaved={onSaved}
          onClose={onClose}
        />
      )}
    </Modal>
  );
}

function MedicationForm({
  documentId,
  draft,
  onSaved,
  onClose,
}: {
  documentId: string;
  draft: MedicationDraft;
  onSaved: () => void;
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const matches = draft.activeMatches;
  const hasMatch = matches.length > 0;
  const [action, setAction] = useState<MedicationFactAction>(
    hasMatch ? "dose_change" : "new",
  );
  const [targetId, setTargetId] = useState(matches[0]?.id ?? "");
  const [drug, setDrug] = useState(draft.prefill.drug ?? "");
  const [dose, setDose] = useState(draft.prefill.dose ?? "");
  const [posology, setPosology] = useState(draft.prefill.posology ?? "");
  const [startedOn, setStartedOn] = useState(draft.prefill.startedOn ?? "");
  const [endedOn, setEndedOn] = useState(draft.prefill.endedOn ?? "");
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [message, setMessage] = useState<string | null>(null);

  const stopping = action === "stop";
  const changing = action === "dose_change";

  const save = useMutation({
    mutationFn: () => {
      // A suspension only needs the date it ended on; the server falls back to startedOn.
      const stopDate = endedOn || startedOn;
      return medicationTimelineApi.record(documentId, draft.factId, {
        action,
        targetMedicationId: action === "new" ? undefined : targetId,
        drug:
          drug.trim() || (matches.find((m) => m.id === targetId)?.drug ?? ""),
        dose: stopping ? undefined : dose.trim() || undefined,
        posology: stopping ? undefined : posology.trim() || undefined,
        startedOn: stopping ? stopDate : startedOn,
        endedOn: stopping
          ? stopDate
          : action === "new" && endedOn
            ? endedOn
            : undefined,
      });
    },
    onSuccess: () => onSaved(),
    onError: (e) => {
      const fe = fieldErrors(e);
      setErrors(fe);
      if (e instanceof ApiError && e.code && CONFLICT_CODES.includes(e.code)) {
        setMessage(t(`documentFacts.medication.errors.${e.code}`));
        // The fact changed elsewhere: reload the lists so they are not stale.
        if (e.code === "already_recorded" || e.code === "not_pending") {
          void queryClient.invalidateQueries({
            queryKey: ["document-facts", documentId],
          });
          void queryClient.invalidateQueries({
            queryKey: ["external-documents"],
          });
          void queryClient.invalidateQueries({ queryKey: ["declared-facts"] });
        }
      } else if (e instanceof ApiError && e.status === 400) {
        setMessage(t("documentFacts.medication.errors.invalid"));
      } else {
        setMessage(t("documentFacts.medication.errors.failed"));
      }
    },
  });

  const canSave =
    (stopping || drug.trim() !== "") &&
    (stopping ? !!(endedOn || startedOn) : startedOn !== "") &&
    (action === "new" || targetId !== "");
  const choices: MedicationFactAction[] = ["dose_change", "stop", "new"];

  return (
    <form
      className="flex flex-col gap-4 p-5"
      onSubmit={(e) => {
        e.preventDefault();
        setErrors({});
        setMessage(null);
        save.mutate();
      }}
    >
      <p className="text-body-md text-onSurfaceVariant">
        {t("documentFacts.medication.intro")}
      </p>
      <div className="rounded-radii-lg border border-outlineVariant p-3">
        <p className="text-label-md text-onSurfaceVariant">
          {t("documentFacts.medication.fromDocument")}
        </p>
        <p className="break-words text-body-md text-onSurface">{draft.value}</p>
        {draft.quote && (
          <p className="mt-1 break-words text-body-sm italic text-onSurfaceVariant">
            “{draft.quote}”
          </p>
        )}
      </div>

      {hasMatch && (
        <fieldset className="flex flex-col gap-2">
          <legend className="mb-1 text-body-md text-onSurface">
            {t("documentFacts.medication.activeFound", {
              dose: matches[0].dose ?? t("documentFacts.medication.noDose"),
              date: formatDate(
                `${matches[0].startedOn}T12:00:00Z`,
                i18n.language,
              ),
            })}
          </legend>
          {choices.map((c) => (
            <label
              key={c}
              className="flex items-center gap-2 rounded-radii-md px-2 py-1 text-body-md text-onSurface hover:bg-pastelLavender"
            >
              <input
                type="radio"
                name="medication-action"
                checked={action === c}
                onChange={() => setAction(c)}
              />
              {t(`documentFacts.medication.choice.${c}`)}
            </label>
          ))}
          {action !== "new" && matches.length > 1 && (
            <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
              {t("documentFacts.medication.target")}
              <select
                className={inputClass}
                value={targetId}
                onChange={(e) => setTargetId(e.target.value)}
              >
                {matches.map((m) => (
                  <option key={m.id} value={m.id}>
                    {[
                      m.drug,
                      m.dose,
                      formatDate(`${m.startedOn}T12:00:00Z`, i18n.language),
                    ]
                      .filter(Boolean)
                      .join(" · ")}
                  </option>
                ))}
              </select>
            </label>
          )}
          {errors.targetMedicationId && (
            <span className="text-label-md text-error">
              {errors.targetMedicationId[0]}
            </span>
          )}
        </fieldset>
      )}

      {!stopping && (
        <FormField
          label={t("documentFacts.medication.drug")}
          value={drug}
          onChange={(e) => setDrug(e.target.value)}
          maxLength={200}
          error={errors.drug}
          required
        />
      )}
      {!stopping && (
        <div className="grid gap-4 sm:grid-cols-2">
          <FormField
            label={t("documentFacts.medication.dose")}
            value={dose}
            onChange={(e) => setDose(e.target.value)}
            maxLength={100}
            error={errors.dose}
          />
          <FormField
            label={t("documentFacts.medication.posology")}
            value={posology}
            onChange={(e) => setPosology(e.target.value)}
            maxLength={200}
            error={errors.posology}
          />
        </div>
      )}
      <div className="grid gap-4 sm:grid-cols-2">
        {stopping ? (
          <FormField
            label={t("documentFacts.medication.stoppedOn")}
            type="date"
            value={endedOn || startedOn}
            onChange={(e) => setEndedOn(e.target.value)}
            error={errors.endedOn ?? errors.startedOn}
            required
          />
        ) : (
          <>
            <FormField
              label={
                changing
                  ? t("documentFacts.medication.changedOn")
                  : t("documentFacts.medication.startedOn")
              }
              type="date"
              value={startedOn}
              onChange={(e) => setStartedOn(e.target.value)}
              error={errors.startedOn}
              required
            />
            {action === "new" && (
              <FormField
                label={t("documentFacts.medication.endedOn")}
                type="date"
                value={endedOn}
                onChange={(e) => setEndedOn(e.target.value)}
                error={errors.endedOn}
              />
            )}
          </>
        )}
      </div>

      {message && (
        <p role="alert" className="text-body-md text-error">
          {message}
        </p>
      )}
      <div className="flex justify-end gap-2">
        <button
          type="button"
          onClick={onClose}
          className="rounded-radii-lg px-4 py-2 text-label-lg text-onSurfaceVariant hover:bg-pastelLavender"
        >
          {t("common.cancel")}
        </button>
        <button
          type="submit"
          disabled={save.isPending || !canSave}
          className="rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary hover:opacity-90 disabled:opacity-60"
        >
          {t("documentFacts.medication.save")}
        </button>
      </div>
    </form>
  );
}
