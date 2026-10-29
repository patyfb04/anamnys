import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { complianceApi } from "@anamnys/shared/api/compliance";
import { toast } from "@anamnys/shared/ui/Toaster";
import { inputClass } from "@/components/patients/FormField";

const MIN_GUARD_YEARS = 5;

// Guard period of the record and the age at which an unsigned note is flagged.
export default function RetentionSettingsSection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data } = useQuery({ queryKey: ["retention-settings"], queryFn: complianceApi.getSettings });
  const [years, setYears] = useState("5");
  const [days, setDays] = useState("7");
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  useEffect(() => {
    if (data) {
      setYears(String(data.guardYears));
      setDays(String(data.unsignedNoteDays));
    }
  }, [data]);

  const yearsValue = Number(years);
  const daysValue = Number(days);
  const valid = Number.isInteger(yearsValue) && yearsValue >= MIN_GUARD_YEARS && Number.isInteger(daysValue) && daysValue >= 1;

  const save = useMutation({
    mutationFn: () => complianceApi.saveSettings({ guardYears: yearsValue, unsignedNoteDays: daysValue }),
    onSuccess: () => {
      setFieldErrors({});
      toast.success(t("settings.retention.saved"));
      void queryClient.invalidateQueries({ queryKey: ["retention-settings"] });
      void queryClient.invalidateQueries({ queryKey: ["compliance"] });
    },
    onError: (e) => {
      setFieldErrors(e instanceof ApiError && e.errors ? e.errors : {});
      toast.error(t("settings.retention.saveFailed"));
    },
  });

  return (
    <section className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md p-6 flex flex-col gap-4">
      <div>
        <h2 className="text-title-md text-onSurface">{t("settings.retention.title")}</h2>
        <p className="text-body-md text-onSurfaceVariant">{t("settings.retention.description")}</p>
      </div>
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          if (valid) save.mutate();
        }}
      >
        <label className="flex flex-col gap-1 max-w-xs">
          <span className="text-label-lg text-onSurface">{t("settings.retention.guardYears")}</span>
          <input type="number" min={MIN_GUARD_YEARS} step={1} className={inputClass} value={years} onChange={(e) => setYears(e.target.value)} />
          <span className="text-body-sm text-onSurfaceVariant">{t("settings.retention.guardYearsHint")}</span>
          {fieldErrors.guardYears?.map((m) => (
            <span key={m} role="alert" className="text-body-sm text-error">
              {m}
            </span>
          ))}
        </label>
        <label className="flex flex-col gap-1 max-w-xs">
          <span className="text-label-lg text-onSurface">{t("settings.retention.unsignedNoteDays")}</span>
          <input type="number" min={1} step={1} className={inputClass} value={days} onChange={(e) => setDays(e.target.value)} />
          <span className="text-body-sm text-onSurfaceVariant">{t("settings.retention.unsignedNoteDaysHint")}</span>
          {fieldErrors.unsignedNoteDays?.map((m) => (
            <span key={m} role="alert" className="text-body-sm text-error">
              {m}
            </span>
          ))}
        </label>
        <button
          type="submit"
          disabled={!valid || save.isPending}
          className="self-start bg-primary text-onPrimary rounded-radii-md px-4 py-2 text-label-lg disabled:opacity-50"
        >
          {t("settings.retention.save")}
        </button>
      </form>
    </section>
  );
}
