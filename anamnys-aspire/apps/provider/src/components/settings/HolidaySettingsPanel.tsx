import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { availabilityApi } from "@anamnys/shared/api/availability";
import { ApiError } from "@anamnys/shared/api/client";
import { toast } from "@anamnys/shared/ui/Toaster";
import { HOLIDAY_SETTINGS_KEY, OVERLAY_KEY } from "@/hooks/useAvailability";
import { formatDate } from "../calendar/calendarTime";
import Switch from "./Switch";

// "Feriados nacionais" (spec §5): whether the provider works on holidays (off by default) and, when not, which holidays
// still count as days off. A holiday's switch applies to every year (the server keys holidays, not dates).
export default function HolidaySettingsPanel() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data, isError } = useQuery({ queryKey: [HOLIDAY_SETTINGS_KEY], queryFn: availabilityApi.getHolidays });
  const [worksOnHolidays, setWorksOnHolidays] = useState(false);
  const [disabled, setDisabled] = useState<string[]>([]);

  useEffect(() => {
    if (data) {
      setWorksOnHolidays(data.worksOnHolidays);
      setDisabled(data.disabledHolidays);
    }
  }, [data]);

  const save = useMutation({
    mutationFn: () => availabilityApi.saveHolidays(worksOnHolidays, disabled),
    onSuccess: (saved) => {
      queryClient.setQueryData([HOLIDAY_SETTINGS_KEY], saved);
      queryClient.invalidateQueries({ queryKey: [OVERLAY_KEY] });
      toast.success(t("availability.holidays.saved"));
    },
    onError: (err) => toast.error(err instanceof ApiError && err.status === 400 ? err.message : t("availability.holidays.saveFailed")),
  });

  const toggle = (key: string, off: boolean) =>
    setDisabled((current) => (off ? current.filter((k) => k !== key) : [...new Set([...current, key])]));

  return (
    <div className="flex flex-col gap-3 border-t border-outlineVariant pt-5">
      <div>
        <h3 className="text-title-md text-onSurface">{t("availability.holidays.title")}</h3>
        <p className="text-body-md text-onSurfaceVariant">{t("availability.holidays.description")}</p>
      </div>
      {isError && <p className="text-body-md text-error">{t("availability.holidays.loadFailed")}</p>}
      <label className="flex items-center gap-3 text-body-md text-onSurface">
        <Switch checked={worksOnHolidays} onChange={setWorksOnHolidays} label={t("availability.holidays.worksOnHolidays")} />
        {t("availability.holidays.worksOnHolidays")}
      </label>
      {data && (
        <>
          <p className="text-body-sm text-onSurfaceVariant">
            {worksOnHolidays ? t("availability.holidays.worksHint") : t("availability.holidays.listHint")}
          </p>
          <ul className={`flex flex-col divide-y divide-outlineVariant rounded-radii-md border border-outlineVariant ${worksOnHolidays ? "opacity-60" : ""}`}>
            {data.holidays.map((h) => {
              const off = !disabled.includes(h.key);
              return (
                <li key={`${h.key}-${h.date}`} className="flex items-center justify-between gap-3 px-3 py-2">
                  <span className="flex min-w-0 flex-wrap items-baseline gap-x-3">
                    <span className="w-24 text-body-md text-onSurfaceVariant">{formatDate(h.date)}</span>
                    <span className="text-body-md text-onSurface">{h.name}</span>
                  </span>
                  <span className="flex items-center gap-2 text-body-sm text-onSurfaceVariant">
                    {off ? t("availability.holidays.dayOff") : t("availability.holidays.working")}
                    <Switch
                      checked={off}
                      disabled={worksOnHolidays}
                      onChange={(next) => toggle(h.key, next)}
                      label={t("availability.holidays.dayOffFor", { name: h.name })}
                    />
                  </span>
                </li>
              );
            })}
          </ul>
        </>
      )}
      <button
        type="button"
        onClick={() => save.mutate()}
        disabled={save.isPending || !data}
        className="self-start rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-50"
      >
        {t("availability.holidays.save")}
      </button>
    </div>
  );
}
