import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Plus, Trash2 } from "lucide-react";
import { patientsApi } from "@anamnys/shared/api/patients";
import { ApiError } from "@anamnys/shared/api/client";
import type { PatientDetail } from "@anamnys/shared/lib/types";
import Modal from "./Modal";
import FormField from "./FormField";
import { todayIso } from "./format";

type Mode = { kind: "create" } | { kind: "edit"; patient: PatientDetail };
type Tab = "basic" | "clinical";
type Errors = Record<string, string[]>;

interface DiagnosisRow { key: number; description: string; icdCode: string }
interface MedicationRow { key: number; drug: string; dose: string; posology: string; startedOn: string }
interface ObjectiveRow { key: number; description: string }

interface Props {
  mode: Mode;
  onClose: () => void;
  onSaved: (patientId: string) => void;
}

let nextKey = 0;
const newKey = () => ++nextKey;

// Create: two tabs, basic data and an optional clinical profile. Edit: basic data only
// (clinical items are edited in place on the detail page). Field errors come from the
// server's ValidationProblem; the modal jumps to the tab holding the first one.
export default function PatientFormModal({ mode, onClose, onSaved }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const editing = mode.kind === "edit" ? mode.patient : null;

  const [tab, setTab] = useState<Tab>("basic");
  const [firstName, setFirstName] = useState(editing?.firstName ?? "");
  const [lastName, setLastName] = useState(editing?.lastName ?? "");
  const [contactEmail, setContactEmail] = useState(editing?.contactEmail ?? "");
  const [dateOfBirth, setDateOfBirth] = useState(editing?.dateOfBirth ?? "");
  const [diagnoses, setDiagnoses] = useState<DiagnosisRow[]>([]);
  const [medications, setMedications] = useState<MedicationRow[]>([]);
  const [objectives, setObjectives] = useState<ObjectiveRow[]>([]);
  const [errors, setErrors] = useState<Errors>({});
  const [formError, setFormError] = useState<string | null>(null);

  const dirty = editing
    ? firstName !== editing.firstName ||
      lastName !== editing.lastName ||
      contactEmail !== (editing.contactEmail ?? "") ||
      dateOfBirth !== (editing.dateOfBirth ?? "")
    : Boolean(firstName || lastName || contactEmail || dateOfBirth || diagnoses.length || medications.length || objectives.length);

  const requestClose = () => {
    if (!dirty || window.confirm(t("patients.form.discardConfirm"))) onClose();
  };

  const mutation = useMutation({
    mutationFn: async () => {
      if (editing) {
        await patientsApi.update(editing.id, { firstName, lastName, contactEmail, dateOfBirth: dateOfBirth || null });
        return editing.id;
      }
      // Rows left completely blank are dropped, so server indexes match the rows shown.
      const keptDiagnoses = diagnoses.filter((d) => d.description.trim() || d.icdCode.trim());
      const keptMedications = medications.filter((m) => m.drug.trim() || m.dose.trim() || m.posology.trim());
      const keptObjectives = objectives.filter((o) => o.description.trim());
      setDiagnoses(keptDiagnoses);
      setMedications(keptMedications);
      setObjectives(keptObjectives);
      return patientsApi.create({
        firstName,
        lastName,
        contactEmail,
        dateOfBirth: dateOfBirth || null,
        diagnoses: keptDiagnoses.map((d) => ({ description: d.description, icdCode: d.icdCode || null })),
        medications: keptMedications.map((m) => ({
          drug: m.drug,
          dose: m.dose || null,
          posology: m.posology || null,
          startedOn: m.startedOn || null,
        })),
        treatmentObjectives: keptObjectives.map((o) => o.description),
      });
    },
    onSuccess: async (id) => {
      await queryClient.invalidateQueries({ queryKey: ["patients"] });
      await queryClient.invalidateQueries({ queryKey: ["patient", id] });
      onSaved(id);
    },
    onError: (e: Error) => {
      if (e instanceof ApiError && e.errors) {
        setErrors(e.errors);
        setFormError(null);
        const keys = Object.keys(e.errors);
        if (!editing && keys.length > 0 && !keys.some((k) => ["firstName", "lastName", "contactEmail", "dateOfBirth"].includes(k))) {
          setTab("clinical");
        } else {
          setTab("basic");
        }
      } else {
        setFormError(t("patients.form.saveFailed"));
      }
    },
  });

  const save = () => {
    setErrors({});
    mutation.mutate();
  };

  const title = editing ? t("patients.form.editTitle") : t("patients.form.createTitle");

  return (
    <Modal
      title={title}
      onClose={requestClose}
      footer={
        <>
          <button
            type="button"
            onClick={requestClose}
            className="px-4 py-2 border border-outlineVariant rounded-radii-md text-label-lg text-onSurface hover:bg-surfaceContainerLow"
          >
            {t("common.cancel")}
          </button>
          <button
            type="button"
            onClick={save}
            disabled={mutation.isPending}
            className="px-5 py-2 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90 disabled:opacity-60"
          >
            {mutation.isPending ? t("patients.form.saving") : t("common.save")}
          </button>
        </>
      }
    >
      {!editing && (
        <div role="tablist" className="flex gap-1 px-5 pt-3 border-b border-outlineVariant">
          {(["basic", "clinical"] as const).map((key) => (
            <button
              key={key}
              type="button"
              role="tab"
              aria-selected={tab === key}
              onClick={() => setTab(key)}
              className={`px-3 py-2 -mb-px border-b-2 text-label-lg transition-colors ${
                tab === key ? "border-primary text-primary" : "border-transparent text-onSurfaceVariant hover:text-onSurface"
              }`}
            >
              {t(`patients.form.tabs.${key}`)}
            </button>
          ))}
        </div>
      )}

      <div className="px-5 py-5">
        {formError && <p className="mb-4 text-body-md text-error">{formError}</p>}

        {tab === "basic" ? (
          <div className="grid gap-4 md:grid-cols-2">
            <FormField
              label={t("patients.form.firstName")}
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              error={errors.firstName}
              autoFocus
            />
            <FormField
              label={t("patients.form.lastName")}
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              error={errors.lastName}
            />
            <div className="flex flex-col gap-1">
              <FormField
                label={t("patients.form.contactEmail")}
                type="email"
                autoComplete="off"
                value={contactEmail}
                onChange={(e) => setContactEmail(e.target.value)}
                error={errors.contactEmail}
              />
              {!errors.contactEmail && (
                <span className="text-label-md text-outline">{t("patients.form.contactEmailHint")}</span>
              )}
            </div>
            <FormField
              label={t("patients.form.dateOfBirth")}
              type="date"
              value={dateOfBirth}
              max={todayIso()}
              onChange={(e) => setDateOfBirth(e.target.value)}
              error={errors.dateOfBirth}
            />
          </div>
        ) : (
          <div className="flex flex-col gap-6">
            <RowSection
              title={t("patients.form.diagnoses")}
              addLabel={t("patients.form.addDiagnosis")}
              listError={errors.diagnoses}
              onAdd={() => setDiagnoses((rows) => [...rows, { key: newKey(), description: "", icdCode: "" }])}
            >
              {diagnoses.map((row, i) => (
                <Row key={row.key} onRemove={() => setDiagnoses((rows) => rows.filter((r) => r.key !== row.key))}>
                  <FormField
                    className="flex-1 min-w-48"
                    label={t("patients.form.diagnosisDescription")}
                    value={row.description}
                    onChange={(e) => setDiagnoses(update(diagnoses, row.key, { description: e.target.value }))}
                    error={errors[`diagnoses[${i}].description`]}
                  />
                  <FormField
                    className="w-28"
                    label={t("patients.form.icdCode")}
                    value={row.icdCode}
                    onChange={(e) => setDiagnoses(update(diagnoses, row.key, { icdCode: e.target.value }))}
                    error={errors[`diagnoses[${i}].icdCode`]}
                  />
                </Row>
              ))}
            </RowSection>

            <RowSection
              title={t("patients.form.medications")}
              addLabel={t("patients.form.addMedication")}
              listError={errors.medications}
              onAdd={() =>
                setMedications((rows) => [...rows, { key: newKey(), drug: "", dose: "", posology: "", startedOn: todayIso() }])
              }
            >
              {medications.map((row, i) => (
                <Row key={row.key} onRemove={() => setMedications((rows) => rows.filter((r) => r.key !== row.key))}>
                  <FormField
                    className="flex-1 min-w-40"
                    label={t("patients.form.drug")}
                    value={row.drug}
                    onChange={(e) => setMedications(update(medications, row.key, { drug: e.target.value }))}
                    error={errors[`medications[${i}].drug`]}
                  />
                  <FormField
                    className="w-28"
                    label={t("patients.form.dose")}
                    value={row.dose}
                    onChange={(e) => setMedications(update(medications, row.key, { dose: e.target.value }))}
                    error={errors[`medications[${i}].dose`]}
                  />
                  <FormField
                    className="w-36"
                    label={t("patients.form.posology")}
                    value={row.posology}
                    onChange={(e) => setMedications(update(medications, row.key, { posology: e.target.value }))}
                    error={errors[`medications[${i}].posology`]}
                  />
                  <FormField
                    className="w-40"
                    label={t("patients.form.startedOn")}
                    type="date"
                    max={todayIso()}
                    value={row.startedOn}
                    onChange={(e) => setMedications(update(medications, row.key, { startedOn: e.target.value }))}
                    error={errors[`medications[${i}].startedOn`]}
                  />
                </Row>
              ))}
            </RowSection>

            <RowSection
              title={t("patients.form.objectives")}
              addLabel={t("patients.form.addObjective")}
              listError={errors.treatmentObjectives}
              onAdd={() => setObjectives((rows) => [...rows, { key: newKey(), description: "" }])}
            >
              {objectives.map((row, i) => (
                <Row key={row.key} onRemove={() => setObjectives((rows) => rows.filter((r) => r.key !== row.key))}>
                  <FormField
                    className="flex-1"
                    label={t("patients.form.objective")}
                    value={row.description}
                    onChange={(e) => setObjectives(update(objectives, row.key, { description: e.target.value }))}
                    error={errors[`treatmentObjectives[${i}]`]}
                  />
                </Row>
              ))}
            </RowSection>
          </div>
        )}
      </div>
    </Modal>
  );
}

function update<T extends { key: number }>(rows: T[], key: number, patch: Partial<T>): T[] {
  return rows.map((row) => (row.key === key ? { ...row, ...patch } : row));
}

function RowSection({
  title,
  addLabel,
  listError,
  onAdd,
  children,
}: {
  title: string;
  addLabel: string;
  listError?: string[];
  onAdd: () => void;
  children: React.ReactNode;
}) {
  return (
    <section>
      <h3 className="text-label-lg text-onSurface mb-2">{title}</h3>
      <div className="flex flex-col gap-3">{children}</div>
      {listError && <p className="mt-1 text-label-md text-error">{listError[0]}</p>}
      <button
        type="button"
        onClick={onAdd}
        className="mt-2 inline-flex items-center gap-1 text-label-lg text-primary hover:underline"
      >
        <Plus size={16} />
        {addLabel}
      </button>
    </section>
  );
}

function Row({ onRemove, children }: { onRemove: () => void; children: React.ReactNode }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-wrap items-start gap-2 p-3 rounded-radii-lg bg-surfaceContainerLow">
      {children}
      <button
        type="button"
        onClick={onRemove}
        aria-label={t("common.remove")}
        className="self-end mb-1 p-2 rounded-radii-full text-onSurfaceVariant hover:text-error hover:bg-surfaceContainer"
      >
        <Trash2 size={18} />
      </button>
    </div>
  );
}
