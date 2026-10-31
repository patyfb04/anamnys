import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import TermSelect from "@/components/themes/TermSelect";
import { Pill, Stethoscope, Target } from "lucide-react";
import { patientsApi } from "@anamnys/shared/api/patients";
import type { DiagnosisItem, MedicationItem, ObjectiveItem } from "@anamnys/shared/lib/types";
import ClinicalListCard from "./ClinicalListCard";
import FormField from "../FormField";
import { formatDay, todayIso } from "../format";

interface CardProps<TItem> {
  patientId: string;
  items: TItem[];
  readOnly: boolean;
  onChanged: () => Promise<void>;
}

export function DiagnosesCard({ patientId, items, readOnly, onChanged }: CardProps<DiagnosisItem>) {
  const { t, i18n } = useTranslation();
  const api = patientsApi.diagnoses;
  return (
    <ClinicalListCard<DiagnosisItem, { description: string; icdCode: string; resolvedOn: string }>
      title={t("patients.detail.diagnoses")}
      icon={<Stethoscope size={20} />}
      emptyLabel={t("patients.detail.noDiagnoses")}
      addLabel={t("patients.detail.add")}
      items={items}
      readOnly={readOnly}
      renderItem={(d) => (
        <div className={d.resolvedOn ? "opacity-60" : undefined}>
          <p className="text-label-lg text-onSurface">
            {d.description}
            {d.icdCode && <span className="ml-2 text-label-md text-onSurfaceVariant">CID {d.icdCode}</span>}
          </p>
          {d.resolvedOn && (
            <p className="text-label-md text-outline">{t("patients.detail.resolvedOn", { date: formatDay(d.resolvedOn, i18n.language) })}</p>
          )}
        </div>
      )}
      emptyDraft={() => ({ description: "", icdCode: "", resolvedOn: "" })}
      toDraft={(d) => ({ description: d.description, icdCode: d.icdCode ?? "", resolvedOn: d.resolvedOn ?? "" })}
      renderFields={(draft, setDraft, errors) => (
        <div className="flex flex-wrap gap-2">
          <FormField
            className="flex-1 min-w-48"
            label={t("patients.form.diagnosisDescription")}
            value={draft.description}
            onChange={(e) => setDraft({ ...draft, description: e.target.value })}
            error={errors.description}
            autoFocus
          />
          <FormField
            className="w-28"
            label={t("patients.form.icdCode")}
            value={draft.icdCode}
            onChange={(e) => setDraft({ ...draft, icdCode: e.target.value })}
            error={errors.icdCode}
          />
        </div>
      )}
      onAdd={async (draft) => {
        await api.add(patientId, { description: draft.description, icdCode: draft.icdCode || null });
        await onChanged();
      }}
      onUpdate={async (d, draft) => {
        await api.update(patientId, d.id, {
          description: draft.description,
          icdCode: draft.icdCode || null,
          resolvedOn: draft.resolvedOn || null,
        });
        await onChanged();
      }}
      onRemove={async (d) => {
        await api.remove(patientId, d.id);
        await onChanged();
      }}
      extraAction={{
        label: (d) => (d.resolvedOn ? t("patients.detail.reopen") : t("patients.detail.resolve")),
        run: async (d) => {
          await api.update(patientId, d.id, {
            description: d.description,
            icdCode: d.icdCode,
            resolvedOn: d.resolvedOn ? null : todayIso(),
          });
          await onChanged();
        },
      }}
    />
  );
}

export function MedicationsCard({ patientId, items, readOnly, onChanged }: CardProps<MedicationItem>) {
  const { t, i18n } = useTranslation();
  const api = patientsApi.medications;
  type Draft = { drug: string; dose: string; posology: string; startedOn: string; endedOn: string };
  const toInput = (d: Draft) => ({
    drug: d.drug,
    dose: d.dose || null,
    posology: d.posology || null,
    startedOn: d.startedOn || null,
    endedOn: d.endedOn || null,
  });
  return (
    <ClinicalListCard<MedicationItem, Draft>
      title={t("patients.detail.medications")}
      icon={<Pill size={20} />}
      emptyLabel={t("patients.detail.noMedications")}
      addLabel={t("patients.detail.add")}
      items={items}
      readOnly={readOnly}
      renderItem={(m) => (
        <div className={m.endedOn ? "opacity-60" : undefined}>
          <p className="text-label-lg text-onSurface">{m.drug}</p>
          {(m.dose || m.posology) && (
            <p className="text-body-md text-onSurfaceVariant">{[m.dose, m.posology].filter(Boolean).join(" · ")}</p>
          )}
          <p className="text-label-md text-outline">
            {m.endedOn
              ? t("patients.detail.endedRange", {
                  from: formatDay(m.startedOn, i18n.language),
                  to: formatDay(m.endedOn, i18n.language),
                })
              : t("patients.detail.since", { date: formatDay(m.startedOn, i18n.language) })}
          </p>
        </div>
      )}
      emptyDraft={() => ({ drug: "", dose: "", posology: "", startedOn: todayIso(), endedOn: "" })}
      toDraft={(m) => ({
        drug: m.drug,
        dose: m.dose ?? "",
        posology: m.posology ?? "",
        startedOn: m.startedOn,
        endedOn: m.endedOn ?? "",
      })}
      renderFields={(draft, setDraft, errors) => (
        <div className="flex flex-wrap gap-2">
          <FormField
            className="flex-1 min-w-40"
            label={t("patients.form.drug")}
            value={draft.drug}
            onChange={(e) => setDraft({ ...draft, drug: e.target.value })}
            error={errors.drug}
            autoFocus
          />
          <FormField
            className="w-28"
            label={t("patients.form.dose")}
            value={draft.dose}
            onChange={(e) => setDraft({ ...draft, dose: e.target.value })}
            error={errors.dose}
          />
          <FormField
            className="w-36"
            label={t("patients.form.posology")}
            value={draft.posology}
            onChange={(e) => setDraft({ ...draft, posology: e.target.value })}
            error={errors.posology}
          />
          <FormField
            className="w-40"
            label={t("patients.form.startedOn")}
            type="date"
            max={todayIso()}
            value={draft.startedOn}
            onChange={(e) => setDraft({ ...draft, startedOn: e.target.value })}
            error={errors.startedOn}
          />
          <FormField
            className="w-40"
            label={t("patients.form.endedOn")}
            type="date"
            max={todayIso()}
            value={draft.endedOn}
            onChange={(e) => setDraft({ ...draft, endedOn: e.target.value })}
            error={errors.endedOn}
          />
        </div>
      )}
      onAdd={async (draft) => {
        await api.add(patientId, toInput(draft));
        await onChanged();
      }}
      onUpdate={async (m, draft) => {
        await api.update(patientId, m.id, toInput(draft));
        await onChanged();
      }}
      onRemove={async (m) => {
        await api.remove(patientId, m.id);
        await onChanged();
      }}
      extraAction={{
        label: (m) => (m.endedOn ? t("patients.detail.resume") : t("patients.detail.end")),
        run: async (m) => {
          await api.update(patientId, m.id, {
            drug: m.drug,
            dose: m.dose,
            posology: m.posology,
            startedOn: m.startedOn,
            endedOn: m.endedOn ? null : todayIso(),
          });
          await onChanged();
        },
      }}
    />
  );
}

export function ObjectivesCard({ patientId, items, readOnly, onChanged }: CardProps<ObjectiveItem>) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const api = patientsApi.objectives;
  // The plan card on "Análises" shows the objectives too.
  const changed = async () => {
    await queryClient.invalidateQueries({ queryKey: ["plan-adherence", patientId] });
    await onChanged();
  };
  type Draft = { description: string; termId: string | null };
  return (
    <ClinicalListCard<ObjectiveItem, Draft>
      title={t("patients.detail.objectives")}
      icon={<Target size={20} />}
      emptyLabel={t("patients.detail.noObjectives")}
      addLabel={t("patients.detail.add")}
      items={items}
      readOnly={readOnly}
      renderItem={(o) => (
        <>
          <p className="text-body-md text-onSurface">{o.description}</p>
          <p className="text-label-md text-outline">
            {o.termLabel ? t("planAdherence.linkedTerm", { label: o.termLabel }) : t("planAdherence.noTerm")}
          </p>
        </>
      )}
      emptyDraft={() => ({ description: "", termId: null })}
      toDraft={(o) => ({ description: o.description, termId: o.termId })}
      renderFields={(draft, setDraft, errors) => (
        <div className="flex flex-col gap-2">
          <FormField
            label={t("patients.form.objective")}
            value={draft.description}
            onChange={(e) => setDraft({ ...draft, description: e.target.value })}
            error={errors.description}
            autoFocus
          />
          <TermSelect
            label={t("planAdherence.termLabel")}
            value={draft.termId}
            currentLabel={items.find((o) => o.termId === draft.termId)?.termLabel}
            onChange={(termId) => setDraft({ ...draft, termId })}
            error={errors.termId?.[0]}
            className="sm:max-w-xs"
          />
        </div>
      )}
      onAdd={async (draft) => {
        await api.add(patientId, draft);
        await changed();
      }}
      onUpdate={async (o, draft) => {
        // PUT replaces the objective: the term is always sent back.
        await api.update(patientId, o.id, draft);
        await changed();
      }}
      onRemove={async (o) => {
        await api.remove(patientId, o.id);
        await changed();
      }}
    />
  );
}
