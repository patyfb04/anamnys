import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { documentsApi } from "@anamnys/shared/api/documents";
import { toast } from "@anamnys/shared/ui/Toaster";
import type { DocumentCheckMode } from "@anamnys/shared/lib/types";

const MODES: DocumentCheckMode[] = ["blocking", "advisory"];

// Whether a missing mandatory element of the CFP 006/2019 check blocks signing or only warns.
export default function DocumentSettingsSection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data } = useQuery({ queryKey: ["document-settings"], queryFn: documentsApi.getSettings });
  const [mode, setMode] = useState<DocumentCheckMode>("blocking");

  useEffect(() => {
    if (data) setMode(data.documentCheckMode);
  }, [data]);

  const save = useMutation({
    mutationFn: () => documentsApi.saveSettings(mode),
    onSuccess: () => {
      toast.success(t("settings.documentCheck.saved"));
      void queryClient.invalidateQueries({ queryKey: ["document-settings"] });
    },
    onError: () => toast.error(t("settings.documentCheck.saveFailed")),
  });

  return (
    <section className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md p-6 flex flex-col gap-4 max-w-xl">
      <div>
        <h2 className="text-title-md text-onSurface">{t("settings.documentCheck.title")}</h2>
        <p className="text-body-md text-onSurfaceVariant">{t("settings.documentCheck.description")}</p>
      </div>
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          save.mutate();
        }}
      >
        <fieldset className="flex flex-col gap-2">
          <legend className="sr-only">{t("settings.documentCheck.title")}</legend>
          {MODES.map((m) => (
            <label key={m} className="flex items-center gap-2 text-body-md text-onSurface">
              <input type="radio" name="documentCheckMode" value={m} checked={mode === m} onChange={() => setMode(m)} />
              {t(`settings.documentCheck.mode.${m}`)}
            </label>
          ))}
        </fieldset>
        <button
          type="submit"
          disabled={save.isPending}
          className="self-start bg-primary text-onPrimary rounded-radii-md px-4 py-2 text-label-lg disabled:opacity-50"
        >
          {t("settings.documentCheck.save")}
        </button>
      </form>
    </section>
  );
}
