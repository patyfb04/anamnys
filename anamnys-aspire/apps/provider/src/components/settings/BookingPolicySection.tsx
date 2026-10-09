import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { bookingPolicyApi } from "@anamnys/shared/api/bookingPolicy";
import { ApiError } from "@anamnys/shared/api/client";
import { toast } from "@anamnys/shared/ui/Toaster";
import type { AutoCancelMode } from "@anamnys/shared/lib/types";
import FormField from "../patients/FormField";

const MODES: AutoCancelMode[] = ["off", "after_email", "before_session"];

// Provider's automatic-cancellation rule for appointments the patient has not confirmed.
export default function BookingPolicySection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data } = useQuery({ queryKey: ["booking-policy"], queryFn: bookingPolicyApi.get });
  const [mode, setMode] = useState<AutoCancelMode>("after_email");
  const [hours, setHours] = useState("1");
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  useEffect(() => {
    if (data) {
      setMode(data.autoCancelMode);
      setHours(String(data.autoCancelHours));
    }
  }, [data]);

  const save = useMutation({
    mutationFn: () => {
      const n = Number(hours);
      const valid = Number.isInteger(n) && n >= 1 && n <= 168;
      // With the rule off the field is disabled and may have been cleared: send the last valid value.
      const autoCancelHours = mode === "off" && !valid ? (data?.autoCancelHours ?? 1) : n;
      return bookingPolicyApi.save({ autoCancelMode: mode, autoCancelHours });
    },
    onSuccess: () => {
      setErrors({});
      toast.success(t("settings.bookingPolicy.saved"));
      queryClient.invalidateQueries({ queryKey: ["booking-policy"] });
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 400 && err.errors) {
        setErrors(err.errors);
      } else {
        toast.error(t("settings.bookingPolicy.saveFailed"));
      }
    },
  });

  return (
    <section className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md p-6 flex flex-col gap-4 max-w-xl">
      <div>
        <h2 className="text-title-md text-onSurface">{t("settings.bookingPolicy.title")}</h2>
        <p className="text-body-md text-onSurfaceVariant">{t("settings.bookingPolicy.description")}</p>
      </div>
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          save.mutate();
        }}
      >
        <fieldset className="flex flex-col gap-2">
          <legend className="sr-only">{t("settings.bookingPolicy.title")}</legend>
          {MODES.map((m) => (
            <label key={m} className="flex items-center gap-2 text-body-md text-onSurface">
              <input
                type="radio"
                name="autoCancelMode"
                value={m}
                checked={mode === m}
                onChange={() => setMode(m)}
              />
              {t(`settings.bookingPolicy.mode.${m}`)}
            </label>
          ))}
          {errors.autoCancelMode && <span className="text-label-md text-error">{errors.autoCancelMode[0]}</span>}
        </fieldset>
        <FormField
          label={t("settings.bookingPolicy.hours")}
          type="number"
          min={1}
          max={168}
          value={hours}
          disabled={mode === "off"}
          error={errors.autoCancelHours}
          onChange={(e) => setHours(e.target.value)}
          className="w-32"
        />
        <button
          type="submit"
          disabled={save.isPending}
          className="self-start bg-primary text-onPrimary rounded-radii-md px-4 py-2 text-label-lg disabled:opacity-50"
        >
          {t("settings.bookingPolicy.save")}
        </button>
      </form>
    </section>
  );
}
