import { useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { patientsApi } from "@anamnys/shared/api/patients";
import { SUPPORTED_LANGUAGES } from "@anamnys/shared/lib/types";
import TopBar from "@anamnys/shared/ui/TopBar";
import Card from "@anamnys/shared/ui/Card";
import Avatar from "@anamnys/shared/ui/Avatar";
import Button from "@anamnys/shared/ui/Button";

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString();
}

// Maps a language code to its display label, defaulting to "Auto-detect" when unset.
function languageLabel(t: (key: string) => string, code?: string): string {
  if (!code) return t("common.autoDetect");
  return SUPPORTED_LANGUAGES.find((l) => l.code === code)?.label ?? code;
}

// Shows a patient's full profile (diagnoses, medications, treatment plan) with a link to their notes.
export default function PatientDetailView({ patientId }: { patientId: string }) {
  const navigate = useNavigate();
  const { t } = useTranslation();

  const { data: patient, isLoading, error } = useQuery({
    queryKey: ["patient", patientId],
    queryFn: () => patientsApi.get(patientId),
  });

  if (isLoading) {
    return (
      <div className="min-h-full flex flex-col">
        <TopBar />
        <div className="flex-1 flex justify-center items-center">
          <Loader2 className="animate-spin text-primary" size={32} />
        </div>
      </div>
    );
  }

  if (error || !patient) {
    return (
      <div className="min-h-full flex flex-col">
        <TopBar />
        <div className="flex-1 flex justify-center items-center">
          <p className="text-error">{t("patients.detail.failedToLoad")}</p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-full flex flex-col">
      <TopBar />
      <div className="p-4 pb-10 max-w-2xl mx-auto w-full">
        <Card className="mb-3 flex items-center gap-4">
          <Avatar name={`${patient.firstName} ${patient.lastName}`} size={64} />
          <div className="min-w-0">
            <p className="text-headline-md text-[20px] text-onSurface">
              {patient.firstName} {patient.lastName}
            </p>
            <p className="text-body-md text-onSurfaceVariant mt-0.5">
              {t("patients.detail.dob", { date: formatDate(patient.dateOfBirth) })}
            </p>
            <p className="text-body-md text-onSurfaceVariant mt-0.5">
              {t("patients.detail.language", { language: languageLabel(t, patient.preferredLanguage) })}
            </p>
            {patient.lastVisit && (
              <p className="text-body-md text-onSurfaceVariant mt-0.5">
                {t("patients.detail.lastVisit", { date: formatDate(patient.lastVisit) })}
              </p>
            )}
          </div>
        </Card>

        {patient.diagnoses.length > 0 && (
          <Card className="mb-3">
            <p className="text-label-md text-onSurfaceVariant mb-2.5">{t("patients.detail.diagnoses")}</p>
            {patient.diagnoses.map((d, i) => (
              <p key={i} className="text-body-lg text-onSurfaceVariant leading-relaxed">
                • {d}
              </p>
            ))}
          </Card>
        )}

        {patient.currentMedications.length > 0 && (
          <Card className="mb-3">
            <p className="text-label-md text-onSurfaceVariant mb-2.5">{t("patients.detail.currentMedications")}</p>
            {patient.currentMedications.map((m, i) => (
              <p key={i} className="text-body-lg text-onSurfaceVariant leading-relaxed">
                • {m}
              </p>
            ))}
          </Card>
        )}

        {patient.treatmentPlan && (
          <Card className="mb-3">
            <p className="text-label-md text-onSurfaceVariant mb-2.5">{t("patients.detail.treatmentPlan")}</p>
            <p className="text-body-lg text-onSurfaceVariant">{patient.treatmentPlan}</p>
          </Card>
        )}

        <Button
          title={t("patients.detail.viewNotes")}
          variant="outline"
          onClick={() => navigate({ to: `/patients/${patientId}/notes` })}
          className="mt-2 mb-2.5"
        />
        <Button
          title={t("patients.detail.newNote")}
          onClick={() => navigate({ to: `/patients/${patientId}/live-transcribe` })}
        />
      </div>
    </div>
  );
}
