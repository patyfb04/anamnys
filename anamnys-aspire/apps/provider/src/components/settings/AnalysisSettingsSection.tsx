import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { analysisApi } from "@anamnys/shared/api/analysis";
import { toast } from "@anamnys/shared/ui/Toaster";
import { inputClass } from "@/components/patients/FormField";

const MIN = 2;
const MAX = 20;

// How many consecutive signed sessions without a record make a theme appear under "sem registro recente".
export default function AnalysisSettingsSection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data } = useQuery({ queryKey: ["analysis-settings"], queryFn: analysisApi.getSettings });
  const [sessions, setSessions] = useState("4");
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  useEffect(() => {
    if (data) setSessions(String(data.absenceSessions));
  }, [data]);

  const value = Number(sessions);
  const valid = Number.isInteger(value) && value >= MIN && value <= MAX;

  const save = useMutation({
    mutationFn: () => analysisApi.saveSettings({ absenceSessions: value }),
    onSuccess: () => {
      setFieldErrors({});
      toast.success(t("settings.analysis.saved"));
      void queryClient.invalidateQueries({ queryKey: ["analysis-settings"] });
      void queryClient.invalidateQueries({ queryKey: ["analysis"] });
    },
    onError: (e) => {
      setFieldErrors(e instanceof ApiError && e.errors ? e.errors : {});
      toast.error(t("settings.analysis.saveFailed"));
    },
  });

  return (
    <section className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md p-6 flex flex-col gap-4">
      <div>
        <h2 className="text-title-md text-onSurface">{t("settings.analysis.title")}</h2>
        <p className="text-body-md text-onSurfaceVariant">{t("settings.analysis.description")}</p>
      </div>
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          if (valid) save.mutate();
        }}
      >
        <label className="flex flex-col gap-1 max-w-xs">
          <span className="text-label-lg text-onSurface">{t("settings.analysis.absenceSessions")}</span>
          <input
            type="number"
            min={MIN}
            max={MAX}
            step={1}
            className={inputClass}
            value={sessions}
            onChange={(e) => setSessions(e.target.value)}
          />
          <span className="text-body-sm text-onSurfaceVariant">{t("settings.analysis.absenceSessionsHint", { min: MIN, max: MAX })}</span>
          {fieldErrors.absenceSessions?.map((m) => (
            <span key={m} role="alert" className="text-body-sm text-error">
              {m}
            </span>
          ))}
        </label>
        <button
          type="submit"
          disabled={!data || !valid || save.isPending}
          className="self-start bg-primary text-onPrimary rounded-radii-md px-4 py-2 text-label-lg disabled:opacity-50"
        >
          {t("settings.analysis.save")}
        </button>
      </form>
    </section>
  );
}
