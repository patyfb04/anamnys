import { createFileRoute, Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ChevronRight } from "lucide-react";
import { portalApi } from "@anamnys/shared/api/portal";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import Avatar from "@anamnys/shared/ui/Avatar";
import { sessionWhen } from "@/components/portal/sessionFormat";

export const Route = createFileRoute("/_app/")({ component: Home });

// "Meus profissionais": the providers whose records are linked to this account, each with
// the next session. See design/specs/2026-10-04-patient-portal-sessions-design.md §4.
function Home() {
  const { t, i18n } = useTranslation();
  const user = useAuthStore((s) => s.user);
  const query = useQuery({ queryKey: ["portal", "providers"], queryFn: portalApi.providers });

  return (
    <div className="max-w-3xl mx-auto px-4 md:px-6 py-8 md:py-10">
      <h1 className="text-headline-lg text-onSurface">{t("dashboard.greeting", { name: user?.name ?? "" })}</h1>
      <h2 className="text-headline-sm text-onSurface mt-8 mb-3">{t("patientPortal.providers.title")}</h2>

      {query.isPending ? (
        <p className="text-body-md text-onSurfaceVariant">{t("patientPortal.loading")}</p>
      ) : query.isError ? (
        <p className="text-body-md text-error">{t("patientPortal.providers.failed")}</p>
      ) : query.data.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant bg-surfaceContainerLowest border border-outlineVariant rounded-radii-xl p-5">
          {t("patientPortal.providers.empty")}
        </p>
      ) : (
        <ul className="flex flex-col gap-3">
          {query.data.map((provider) => (
            <li key={provider.providerId}>
              <Link
                to="/profissionais/$providerId"
                params={{ providerId: provider.providerId }}
                className="flex items-center gap-4 bg-surfaceContainerLowest border border-outlineVariant rounded-radii-xl p-4 hover:bg-surfaceContainerLow transition-colors"
              >
                <Avatar name={provider.name} size={44} />
                <div className="flex-1 min-w-0">
                  <p className="text-label-lg text-onSurface truncate">{provider.name}</p>
                  {provider.crp && <p className="text-label-md text-outline">CRP {provider.crp}</p>}
                  <p className="text-body-md text-onSurfaceVariant mt-1">
                    {provider.nextSession
                      ? `${t("patientPortal.providers.next")}: ${sessionWhen(
                          provider.nextSession,
                          i18n.language,
                          t(`patientPortal.sessions.modality.${provider.nextSession.modality === "presencial" ? "presencial" : "online"}`),
                        )}`
                      : t("patientPortal.providers.noNext")}
                  </p>
                </div>
                <ChevronRight size={20} className="text-outline shrink-0" />
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
