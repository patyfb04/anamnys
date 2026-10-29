import { useMutation, useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { complianceApi } from "@anamnys/shared/api/compliance";
import { toast } from "@anamnys/shared/ui/Toaster";

const dateFormat = new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" });

// Terms issued by past discards, oldest first, each with a link to its PDF.
export default function DisposalTermsList() {
  const { t } = useTranslation();
  const { data, isPending, isError } = useQuery({ queryKey: ["disposal-terms"], queryFn: complianceApi.terms });
  // The tab opens synchronously on click (popup blockers) and is pointed at the link once the server answers.
  const open = useMutation({
    mutationFn: async (id: string) => {
      const w = window.open("", "_blank");
      if (w) w.opener = null;
      try {
        return { w, ...(await complianceApi.termPdf(id)) };
      } catch (e) {
        w?.close();
        throw e;
      }
    },
    onSuccess: ({ w, url }) => {
      if (!/^https?:\/\//i.test(url)) {
        w?.close();
        toast.error(t("compliance.terms.openFailed"));
      } else if (w) w.location.href = url;
      else toast.error(t("compliance.terms.popupBlocked"));
    },
    onError: () => toast.error(t("compliance.terms.openFailed")),
  });

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-title-md text-onSurface">{t("compliance.terms.title")}</h2>
      {isPending ? null : isError ? (
        <p className="text-body-md text-error">{t("compliance.terms.loadFailed")}</p>
      ) : data.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("compliance.terms.empty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-outlineVariant rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest">
          {data.map((term) => (
            <li key={term.id} className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 text-body-md text-onSurface">
              <span>
                {t("compliance.terms.item", {
                  patient: term.patientName,
                  date: dateFormat.format(new Date(term.createdAt)),
                })}
              </span>
              <button
                type="button"
                disabled={open.isPending}
                onClick={() => open.mutate(term.id)}
                className="text-label-lg text-primary hover:underline disabled:opacity-50"
              >
                {t("compliance.terms.openPdf")}
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
