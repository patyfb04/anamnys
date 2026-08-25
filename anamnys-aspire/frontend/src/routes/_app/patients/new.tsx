import { useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { patientsApi } from "@/api/patients";
import TopBar from "@/components/ui/TopBar";
import Card from "@/components/ui/Card";
import TextField from "@/components/ui/TextField";
import Button from "@/components/ui/Button";
import LanguagePicker from "@/components/LanguagePicker";

export const Route = createFileRoute("/_app/patients/new")({
  component: AddPatientPage,
});

// Splits a multiline textarea into a trimmed, non-empty list of lines.
function linesToList(text: string): string[] {
  return text
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

// Form to register a new patient under the authenticated provider.
function AddPatientPage() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");
  const [dateOfBirth, setDateOfBirth] = useState("");
  const [diagnoses, setDiagnoses] = useState("");
  const [currentMedications, setCurrentMedications] = useState("");
  const [treatmentPlan, setTreatmentPlan] = useState("");
  const [preferredLanguage, setPreferredLanguage] = useState<string | undefined>(undefined);

  const createMutation = useMutation({
    mutationFn: () =>
      patientsApi.create({
        firstName: firstName.trim(),
        lastName: lastName.trim(),
        dateOfBirth,
        diagnoses: linesToList(diagnoses),
        currentMedications: linesToList(currentMedications),
        treatmentPlan: treatmentPlan.trim() || undefined,
        preferredLanguage,
      }),
    onSuccess: (patient) => {
      queryClient.invalidateQueries({ queryKey: ["patients"] });
      navigate({ to: `/patients/${patient.id}`, replace: true });
    },
    onError: (e: Error) => {
      alert(`${t("patients.add.createFailedTitle")}: ${e.message}`);
    },
  });

  const handleSubmit = () => {
    if (!firstName.trim() || !lastName.trim()) {
      alert(t("patients.add.missingInfoBody"));
      return;
    }
    if (!dateOfBirth) {
      alert(t("patients.add.invalidDateBody"));
      return;
    }
    createMutation.mutate();
  };

  return (
    <div className="min-h-full flex flex-col">
      <TopBar title={t("patients.add.title")} />
      <div className="p-4 pb-10 max-w-2xl mx-auto w-full">
        <Card className="mb-3">
          <p className="text-label-md text-onSurfaceVariant mb-3.5">{t("patients.add.basicInfo")}</p>
          <TextField
            label={t("patients.add.firstName")}
            value={firstName}
            onChange={(e) => setFirstName(e.target.value)}
            placeholder={t("patients.add.firstNamePlaceholder")}
            containerClassName="mb-3.5"
          />
          <TextField
            label={t("patients.add.lastName")}
            value={lastName}
            onChange={(e) => setLastName(e.target.value)}
            placeholder={t("patients.add.lastNamePlaceholder")}
            containerClassName="mb-3.5"
          />
          <TextField
            label={t("patients.add.dob")}
            type="date"
            value={dateOfBirth}
            onChange={(e) => setDateOfBirth(e.target.value)}
            containerClassName="mb-3.5"
          />
          <label className="block text-label-sm text-onSurfaceVariant mb-2">{t("patients.add.preferredLanguage")}</label>
          <LanguagePicker value={preferredLanguage} onChange={setPreferredLanguage} />
        </Card>

        <Card className="mb-3">
          <p className="text-label-md text-onSurfaceVariant mb-3.5">{t("patients.add.clinicalProfile")}</p>
          <TextField
            label={t("patients.add.diagnoses")}
            value={diagnoses}
            onChange={(e) => setDiagnoses(e.target.value)}
            placeholder={t("patients.add.diagnosesPlaceholder")}
            multiline
            containerClassName="mb-3.5"
          />
          <TextField
            label={t("patients.add.medications")}
            value={currentMedications}
            onChange={(e) => setCurrentMedications(e.target.value)}
            placeholder={t("patients.add.medicationsPlaceholder")}
            multiline
            containerClassName="mb-3.5"
          />
          <TextField
            label={t("patients.add.treatmentPlan")}
            value={treatmentPlan}
            onChange={(e) => setTreatmentPlan(e.target.value)}
            placeholder={t("patients.add.treatmentPlanPlaceholder")}
            multiline
          />
        </Card>

        <Button title={t("patients.add.submit")} onClick={handleSubmit} loading={createMutation.isPending} />
      </div>
    </div>
  );
}
