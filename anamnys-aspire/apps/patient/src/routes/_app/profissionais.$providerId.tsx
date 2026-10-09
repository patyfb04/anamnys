import { useState } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ArrowLeft } from "lucide-react";
import { portalApi } from "@anamnys/shared/api/portal";
import { ApiError } from "@anamnys/shared/api/client";
import Avatar from "@anamnys/shared/ui/Avatar";
import { toast } from "@anamnys/shared/ui/Toaster";
import SessionLine from "@/components/portal/SessionLine";

export const Route = createFileRoute("/_app/profissionais/$providerId")({ component: ProviderPage });

type Tab = "upcoming" | "past";

// One provider's sessions for the signed-in patient: upcoming and history, schedule fields
// only. See design/specs/2026-10-04-patient-portal-sessions-design.md §4.
function ProviderPage() {
  const { providerId } = Route.useParams();
  const { t } = useTranslation();
  const [tab, setTab] = useState<Tab>("upcoming");

  // The list already holds name and CRP; reuse it rather than adding a provider endpoint.
  const providers = useQuery({ queryKey: ["portal", "providers"], queryFn: portalApi.providers });
  const provider = providers.data?.find((p) => p.providerId === providerId);

  const sessions = useInfiniteQuery({
    queryKey: ["portal", "sessions", providerId, tab],
    queryFn: ({ pageParam }) => portalApi.sessions(providerId, tab, pageParam),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.totalCount ? last.page + 1 : undefined),
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 2,
  });

  const queryClient = useQueryClient();
  const confirm = useMutation({
    mutationFn: (appointmentId: string) => portalApi.confirm(appointmentId),
    onSuccess: () => {
      toast.success(t("patientPortal.sessions.confirmed"));
      queryClient.invalidateQueries({ queryKey: ["portal"] });
    },
    onError: () => toast.error(t("patientPortal.sessions.confirmFailed")),
  });

  const notFound =
    (sessions.error instanceof ApiError && sessions.error.status === 404) || (providers.isSuccess && !provider);

  if (notFound) {
    return (
      <div className="max-w-3xl mx-auto px-4 md:px-6 py-8">
        <BackLink />
        <p className="text-body-md text-onSurfaceVariant mt-6">{t("patientPortal.sessions.notFound")}</p>
      </div>
    );
  }

  const items = sessions.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <div className="max-w-3xl mx-auto px-4 md:px-6 py-8">
      <BackLink />

      <header className="flex items-center gap-4 mt-4">
        <Avatar name={provider?.name ?? "?"} size={56} />
        <div className="min-w-0">
          <h1 className="text-headline-md text-onSurface truncate">{provider?.name ?? ""}</h1>
          {provider?.crp && <p className="text-label-md text-outline">CRP {provider.crp}</p>}
        </div>
      </header>

      <div role="tablist" className="flex gap-1 mt-6 border-b border-outlineVariant">
        {(["upcoming", "past"] as const).map((key) => (
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
            {t(`patientPortal.sessions.tabs.${key}`)}
          </button>
        ))}
      </div>

      <section className="bg-surfaceContainerLowest border border-outlineVariant border-t-0 rounded-b-radii-xl px-4">
        {sessions.isPending ? (
          <p className="py-4 text-body-md text-onSurfaceVariant">{t("patientPortal.loading")}</p>
        ) : sessions.isError ? (
          <p className="py-4 text-body-md text-error">{t("patientPortal.sessions.failed")}</p>
        ) : items.length === 0 ? (
          <p className="py-4 text-body-md text-onSurfaceVariant">
            {tab === "upcoming" ? t("patientPortal.sessions.noUpcoming") : t("patientPortal.sessions.noPast")}
          </p>
        ) : (
          <>
            <ul className="divide-y divide-outlineVariant">
              {items.map((session) => (
                <SessionLine
                  key={`${session.startsAt}-${session.status}`}
                  session={session}
                  onConfirm={tab === "upcoming" ? (s) => confirm.mutate(s.id) : undefined}
                  confirming={confirm.isPending && confirm.variables === session.id}
                />
              ))}
            </ul>
            {sessions.hasNextPage && (
              <div className="py-3 border-t border-outlineVariant">
                <button
                  type="button"
                  onClick={() => sessions.fetchNextPage()}
                  disabled={sessions.isFetchingNextPage}
                  className="text-label-lg text-primary hover:underline disabled:opacity-60"
                >
                  {t("patientPortal.sessions.loadMore")}
                </button>
              </div>
            )}
          </>
        )}
      </section>
    </div>
  );
}

function BackLink() {
  const { t } = useTranslation();
  return (
    <Link to="/" className="inline-flex items-center gap-1 text-label-lg text-primary hover:underline">
      <ArrowLeft size={16} />
      {t("patientPortal.providers.title")}
    </Link>
  );
}
