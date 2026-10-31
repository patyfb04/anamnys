import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ChevronDown, ClipboardList } from "lucide-react";
import { planAdherenceApi } from "@anamnys/shared/api/analysis";
import { ApiError } from "@anamnys/shared/api/client";
import { patientsApi } from "@anamnys/shared/api/patients";
import type { FollowUp, PlanAdherenceObjective } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import { formatInstant, formatShortDate, daysBetweenNow } from "./planModel";

// "Plano terapêutico": the plan's last review, each objective against the theme linked to it, and the points
// marked to resume. Observation only: dates and intervals, neutral colours, no deadline and no alert.
export default function PlanCard({ patientId }: { patientId: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [error, setError] = useState<string | null>(null);
  const [showResolved, setShowResolved] = useState(false);
  const query = useQuery({ queryKey: ["plan-adherence", patientId], queryFn: () => planAdherenceApi.get(patientId) });
  const patient = useQuery({ queryKey: ["patient", patientId], queryFn: () => patientsApi.get(patientId) });
  const readOnly = !!patient.data?.archivedAt;

  const refresh = () => queryClient.invalidateQueries({ queryKey: ["plan-adherence", patientId] });
  const fail = (e: unknown) =>
    setError(
      e instanceof ApiError && e.code === "archived"
        ? t("planAdherence.archived")
        : e instanceof ApiError && e.code === "invalid_note"
          ? t("planAdherence.errors.invalid_note")
          : e instanceof ApiError && e.status === 409
            ? t("planAdherence.errors.conflict")
            : t("planAdherence.actionFailed"),
    );
  const ok = () => {
    setError(null);
    void refresh();
  };
  const review = useMutation({ mutationFn: () => planAdherenceApi.review(patientId), onSuccess: ok, onError: fail });
  const resolve = useMutation({ mutationFn: (id: string) => planAdherenceApi.resolve(id), onSuccess: ok, onError: fail });
  const reopen = useMutation({ mutationFn: (id: string) => planAdherenceApi.reopen(id), onSuccess: ok, onError: fail });

  const data = query.data;
  const open = (data?.followUps ?? []).filter((f) => !f.resolvedAt);
  const resolved = (data?.followUps ?? []).filter((f) => f.resolvedAt);
  const linkClass = "rounded-radii-md px-2 py-1 text-label-md text-primary hover:underline disabled:opacity-60";

  return (
    <DetailCard
      title={t("planAdherence.title")}
      icon={<ClipboardList size={20} />}
      action={
        !readOnly && data ? (
          <button type="button" disabled={review.isPending} onClick={() => review.mutate()} className="text-label-lg text-primary hover:underline disabled:opacity-60">
            {t("planAdherence.markReviewed")}
          </button>
        ) : null
      }
    >
      {query.isPending ? null : query.isError || !data ? (
        <p role="alert" className="text-body-md text-error">
          {t("planAdherence.loadFailed")}
        </p>
      ) : (
        <div className="flex flex-col gap-5">
          <p className="text-body-md text-onSurfaceVariant">
            {data.plan.reviewedAt
              ? t("planAdherence.reviewed", {
                  date: formatInstant(data.plan.reviewedAt),
                  days: t("planAdherence.daysAgo", { count: daysBetweenNow(data.plan.reviewedAt) }),
                })
              : t("planAdherence.notReviewed")}
          </p>

          <section aria-labelledby={`plan-objectives-${patientId}`} className="flex flex-col gap-1">
            <h3 id={`plan-objectives-${patientId}`} className="text-title-sm text-onSurface">
              {t("planAdherence.objectives")}
            </h3>
            {data.objectives.length === 0 ? (
              <p className="text-body-md text-onSurfaceVariant">{t("planAdherence.noObjectives")}</p>
            ) : (
              <ul className="flex flex-col divide-y divide-outlineVariant">
                {data.objectives.map((o) => (
                  <li key={o.objectiveId} className="flex flex-col gap-0.5 px-2 py-2">
                    <span className="break-words text-body-md text-onSurface">{o.description}</span>
                    {o.term && (
                      <span className="text-label-md text-outline">{t("planAdherence.linkedTerm", { label: o.term.label })}</span>
                    )}
                    <span className="text-body-sm text-onSurfaceVariant">
                      {objectiveLine(o, t)}
                      {o.lastNoteId && (
                        <>
                          {" "}
                          <Link to="/notes/$noteId" params={{ noteId: o.lastNoteId }} className="text-label-md text-primary hover:underline">
                            {t("planAdherence.viewNote")}
                          </Link>
                        </>
                      )}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section aria-labelledby={`plan-followups-${patientId}`} className="flex flex-col gap-1">
            <h3 id={`plan-followups-${patientId}`} className="text-title-sm text-onSurface">
              {t("planAdherence.followUps")}
            </h3>
            {open.length === 0 ? (
              <p className="text-body-md text-onSurfaceVariant">{t("planAdherence.noFollowUps")}</p>
            ) : (
              <ul className="flex flex-col divide-y divide-outlineVariant">
                {open.map((f) => (
                  <FollowUpRow key={f.id} item={f}>
                    {!readOnly && (
                      <button type="button" disabled={resolve.isPending} onClick={() => resolve.mutate(f.id)} className={linkClass}>
                        {t("planAdherence.resolve")}
                      </button>
                    )}
                  </FollowUpRow>
                ))}
              </ul>
            )}
            {resolved.length > 0 && (
              <div className="flex flex-col gap-1">
                <button
                  type="button"
                  onClick={() => setShowResolved((v) => !v)}
                  aria-expanded={showResolved}
                  className="inline-flex items-center gap-1 self-start rounded-radii-md px-2 py-1 text-label-md text-primary hover:underline"
                >
                  {t("planAdherence.resolvedToggle", { count: resolved.length })}
                  <ChevronDown size={16} aria-hidden className={`transition-transform ${showResolved ? "rotate-180" : ""}`} />
                </button>
                {showResolved && (
                  <ul className="flex flex-col divide-y divide-outlineVariant">
                    {resolved.map((f) => (
                      <FollowUpRow key={f.id} item={f}>
                        {!readOnly && (
                          <button type="button" disabled={reopen.isPending} onClick={() => reopen.mutate(f.id)} className={linkClass}>
                            {t("planAdherence.reopen")}
                          </button>
                        )}
                      </FollowUpRow>
                    ))}
                  </ul>
                )}
              </div>
            )}
          </section>
          {error && (
            <p role="alert" className="text-body-sm text-error">
              {error}
            </p>
          )}
        </div>
      )}
    </DetailCard>
  );
}

function objectiveLine(o: PlanAdherenceObjective, t: (key: string, options?: Record<string, unknown>) => string): string {
  const created = formatInstant(o.createdAt);
  if (!o.term || o.sessionsSince === null || o.daysSince === null) return t("planAdherence.objectiveNoTerm", { created });
  const since =
    o.sessionsSince === 0 && o.lastOn
      ? t("planAdherence.sinceLatest")
      : o.sessionsSince === 0
        ? t("planAdherence.sinceNone")
        : t("planAdherence.since", { count: o.sessionsSince, days: t("planAdherence.daysCount", { count: o.daysSince }) });
  return o.lastOn
    ? t("planAdherence.objectiveRecorded", { created, last: formatShortDate(o.lastOn), since })
    : t("planAdherence.objectiveNever", { created, since });
}

function FollowUpRow({ item, children }: { item: FollowUp; children: React.ReactNode }) {
  const { t } = useTranslation();
  const line = item.resolvedAt
    ? t("planAdherence.resolvedLine", { date: formatInstant(item.createdAt), resolved: formatInstant(item.resolvedAt) })
    : t("planAdherence.markedLine", { date: formatInstant(item.createdAt), since: t("planAdherence.followSince", { count: item.sessionsSince }) });
  return (
    <li className="flex items-start gap-2 px-2 py-2">
      <div className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="break-words text-body-md text-onSurface">{item.description}</span>
        <span className="text-body-sm text-onSurfaceVariant">{line}</span>
        <span className="flex flex-wrap gap-3">
          {item.noteId && item.excerpt && (
            <Link
              to="/notes/$noteId"
              params={{ noteId: item.noteId }}
              search={{ excerpt: item.excerpt.excerptId }}
              className="text-label-md text-primary hover:underline"
            >
              {t("planAdherence.viewPassage")}
            </Link>
          )}
          {item.resolvedNoteId && (
            <Link to="/notes/$noteId" params={{ noteId: item.resolvedNoteId }} className="text-label-md text-primary hover:underline">
              {t("planAdherence.viewResolvingNote")}
            </Link>
          )}
        </span>
      </div>
      <div className="shrink-0">{children}</div>
    </li>
  );
}
