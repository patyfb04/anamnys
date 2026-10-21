import { Fragment, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { createFileRoute, Link, useBlocker, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { ArrowLeft, FileDown, FileText, HelpCircle, PenLine } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import { documentsApi } from "@anamnys/shared/api/documents";
import type { DocumentDetail, DocumentKindInfo, DocumentTeamMember } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import DetailCard from "@/components/patients/detail/DetailCard";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import ConformityPanel from "@/components/documents/ConformityPanel";
import DocumentHelpPanel from "@/components/documents/DocumentHelpPanel";
import DocumentStatusBadge from "@/components/documents/DocumentStatusBadge";
import TeamEditor from "@/components/documents/TeamEditor";

export const Route = createFileRoute("/_app/documents/$documentId")({
  component: DocumentPage,
});

const textareaClass =
  "w-full min-h-28 resize-y bg-surfaceContainerLowest border border-outlineVariant focus:border-primary rounded-radii-md px-3 py-2 text-body-md text-onSurface outline-none read-only:bg-surfaceContainerLow";

const PRAZO = "prazo_validade";

function DocumentPage() {
  const { documentId } = Route.useParams();
  const { t } = useTranslation();
  const detail = useQuery({ queryKey: ["document", documentId], queryFn: () => documentsApi.get(documentId) });
  const kinds = useQuery({ queryKey: ["document-kinds"], queryFn: documentsApi.kinds });
  const kind = kinds.data?.find((k) => k.kind === detail.data?.kind);

  if (detail.isError) return <p className="p-8 text-body-lg text-error">{t("documents.notFound")}</p>;
  if (!detail.data || !kind) return null;
  // Keyed so the form restarts from the server state when the document changes status
  // (signed here or elsewhere) and drops any stale unsaved edits.
  const d = detail.data;
  return <DocumentEditor key={`${d.id}-${d.status}-${d.signedAt ?? ""}`} detail={d} kind={kind} />;
}

function sameSet(a: string[], b: string[]) {
  return a.length === b.length && a.every((x) => b.includes(x));
}

function DocumentEditor({ detail, kind }: { detail: DocumentDetail; kind: DocumentKindInfo }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [fields, setFields] = useState<Record<string, string>>(detail.fields);
  const [confirmations, setConfirmations] = useState<string[]>(detail.confirmations);
  const [team, setTeam] = useState<DocumentTeamMember[]>(detail.team ?? []);
  const [teamErrors, setTeamErrors] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState(false);
  const [signing, setSigning] = useState(false);
  const [signError, setSignError] = useState<string | null>(null);
  const [helping, setHelping] = useState(false);

  const draft = detail.status === "draft";
  const [signErrorCode, setSignErrorCode] = useState<string | null>(null);
  // Prazo de validade is always the last paragraph of the document, so it is the last field too.
  const providerElements = kind.elements.filter((e) => e.origin === "provider");
  const ordered = [...providerElements.filter((e) => e.code !== PRAZO), ...providerElements.filter((e) => e.code === PRAZO)];
  const dirty =
    draft &&
    (!sameSet(confirmations, detail.confirmations) ||
      JSON.stringify(team) !== JSON.stringify(detail.team ?? []) ||
      ordered.some((e) => (fields[e.code] ?? "") !== (detail.fields[e.code] ?? "")));

  // In-app navigation asks through a dialog; closing or reloading the tab gets the browser's prompt.
  const blocker = useBlocker({
    shouldBlockFn: () => dirty,
    enableBeforeUnload: () => dirty,
    withResolver: true,
  });

  const store = (next: DocumentDetail) => {
    queryClient.setQueryData(["document", detail.id], next);
    void queryClient.invalidateQueries({ queryKey: ["documents", detail.patientId] });
  };
  const refetch = async () => {
    await queryClient.invalidateQueries({ queryKey: ["document", detail.id] });
    await queryClient.invalidateQueries({ queryKey: ["documents", detail.patientId] });
  };

  const save = async () => {
    setSaving(true);
    setSignError(null);
    try {
      const payload = Object.fromEntries(ordered.map((e) => [e.code, fields[e.code] ?? ""]));
      const next = await documentsApi.save(detail.id, payload, confirmations, team);
      // The server trims values: re-seed so the form matches what was stored.
      setFields(next.fields);
      setConfirmations(next.confirmations);
      setTeam(next.team ?? []);
      setTeamErrors({});
      store(next);
      toast.success(t("documents.saved"));
    } catch (e) {
      const teamKeys =
        e instanceof ApiError && e.status === 400 && e.errors
          ? Object.entries(e.errors).filter(([k]) => k === "team" || k.startsWith("team["))
          : [];
      if (teamKeys.length > 0) {
        setTeamErrors(Object.fromEntries(teamKeys.map(([k, v]) => [k, v[0]])));
        toast.error(t("documents.saveFailed"));
        return;
      }
      toast.error(e instanceof ApiError && e.status === 409 ? e.message : t("documents.saveFailed"));
      if (e instanceof ApiError && e.code === "locked") await refetch();
    } finally {
      setSaving(false);
    }
  };

  const sign = async (acknowledgeMissing: boolean) => {
    setSigning(true);
    setSignError(null);
    setSignErrorCode(null);
    try {
      await documentsApi.sign(detail.id, acknowledgeMissing);
      await refetch();
    } catch (e) {
      // The refreshed check shows what is still missing.
      setSignError(e instanceof ApiError ? e.message : t("documents.signFailed"));
      setSignErrorCode(e instanceof ApiError ? (e.code ?? null) : null);
      if (e instanceof ApiError && e.status === 409) await refetch();
    } finally {
      setSigning(false);
    }
  };

  return (
    <div className="flex flex-col gap-6 p-4 md:p-8">
      <Link
        to="/patients/$patientId"
        params={{ patientId: detail.patientId }}
        className="inline-flex items-center gap-1 self-start text-label-lg text-primary hover:underline"
      >
        <ArrowLeft size={16} />
        {t("documents.backToPatient")}
      </Link>

      <header className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <h1 className="truncate text-headline-lg text-onSurface">{detail.title}</h1>
          <div className="mt-1 flex flex-wrap items-center gap-2 text-body-md text-onSurfaceVariant">
            <span>{detail.patientName}</span>
            <DocumentStatusBadge status={detail.status} />
            <span>
              {detail.signedAt
                ? t("documents.signedAt", { date: new Date(detail.signedAt).toLocaleString("pt-BR"), crp: detail.signedByCrp ?? "—" })
                : new Date(detail.createdAt).toLocaleString("pt-BR")}
            </span>
          </div>
        </div>
        <button
          type="button"
          onClick={() => setHelping(true)}
          aria-label={t("documents.helpLabel")}
          className="rounded-radii-full p-1.5 text-onSurfaceVariant hover:bg-pastelLavender"
        >
          <HelpCircle size={20} />
        </button>
      </header>

      {detail.status === "superseded" && detail.supersededBy && (
        <p className="rounded-radii-lg bg-surfaceContainerLow p-4 text-body-md text-onSurface">
          {t("documents.supersededBanner")}{" "}
          <Link to="/documents/$documentId" params={{ documentId: detail.supersededBy }} className="text-primary hover:underline">
            {t("documents.openReplacement")}
          </Link>
        </p>
      )}

      <div className="grid gap-6 lg:grid-cols-3">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <DetailCard title={t("documents.systemTitle")} icon={<FileText size={20} />}>
            <dl className="grid gap-3 sm:grid-cols-2">
              <SystemValue label={t("documents.system.patient")} value={detail.patientName} />
              <SystemValue
                label={t("documents.system.provider")}
                value={
                  detail.signedByCrp
                    ? [detail.signedByName, t("documents.system.crp", { crp: detail.signedByCrp })].filter(Boolean).join(" · ")
                    : detail.providerHasCrp
                      ? t("documents.system.fromProfile")
                      : null
                }
                missing={t("documents.system.needsCrp")}
                showLink={draft}
              />
              <SystemValue
                label={t("documents.system.city")}
                value={detail.signedCity ?? (detail.hasPracticeCity ? t("documents.system.fromProfile") : null)}
                missing={t("documents.system.needsCity")}
                showLink={draft}
              />
              <SystemValue label={t("documents.system.date")} value={t("documents.system.signingDate")} />
            </dl>
          </DetailCard>

          <DetailCard title={t("documents.formTitle")} icon={<PenLine size={20} />}>
            <div className="flex flex-col gap-5">
              {detail.kind === "atestado" && <p className="text-body-sm text-onSurfaceVariant">{t("documents.atestadoHint")}</p>}
              {detail.kind === "laudo" && <p className="text-body-sm text-onSurfaceVariant">{t("documents.laudoHint")}</p>}
              {detail.kind === "parecer" && <p className="text-body-sm text-onSurfaceVariant">{t("documents.parecerHint")}</p>}
              {ordered.map((el) => (
                <Fragment key={el.code}>
                  <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
                    <span className="text-label-lg text-onSurface">{el.label}</span>
                    {el.hint && <span className="text-body-sm">{el.hint}</span>}
                    <textarea
                      className={textareaClass}
                      value={fields[el.code] ?? ""}
                      readOnly={!draft || saving}
                      maxLength={20000}
                      onChange={(e) => setFields((f) => ({ ...f, [el.code]: e.target.value }))}
                    />
                  </label>
                  {detail.kind === "relatorio_multiprofissional" && el.code === "analise" && (
                    <section className="flex flex-col gap-2">
                      <h3 className="text-label-lg text-onSurface">{t("documents.teamTitle")}</h3>
                      <p className="text-body-sm text-onSurfaceVariant">{t("documents.teamHint")}</p>
                      <TeamEditor members={team} onChange={(next) => { setTeam(next); setTeamErrors({}); }} readOnly={!draft || saving} errors={teamErrors} />
                    </section>
                  )}
                </Fragment>
              ))}
            </div>
            {draft && (
              <button
                type="button"
                disabled={saving || !dirty}
                onClick={() => void save()}
                className="mt-5 rounded-radii-lg bg-primary px-5 py-2.5 text-label-lg text-onPrimary disabled:opacity-60"
              >
                {t("documents.save")}
              </button>
            )}
          </DetailCard>
        </div>

        <div className="flex flex-col gap-6">
          <ConformityPanel
            kind={kind}
            detail={detail}
            confirmations={confirmations}
            dirty={dirty}
            signing={signing}
            saving={saving}
            error={signError}
            errorCode={signErrorCode}
            onToggle={(code, checked) =>
              setConfirmations((c) => (checked ? [...c.filter((x) => x !== code), code] : c.filter((x) => x !== code)))
            }
            onSign={(ack) => void sign(ack)}
          />
          {detail.signedAt && (
            <SignedActions
              detail={detail}
              onExported={refetch}
              onCorrected={(id) => {
                void queryClient.invalidateQueries({ queryKey: ["documents", detail.patientId] });
                void navigate({ to: "/documents/$documentId", params: { documentId: id } });
              }}
            />
          )}
        </div>
      </div>

      {helping && <DocumentHelpPanel onClose={() => setHelping(false)} />}
      <ConfirmDialog
        open={blocker.status === "blocked"}
        onOpenChange={(open) => {
          if (!open && blocker.status === "blocked") blocker.reset();
        }}
        title={t("documents.leaveTitle")}
        description={t("documents.leaveWarning")}
        confirmLabel={t("documents.leave")}
        destructive
        onConfirm={() => blocker.status === "blocked" && blocker.proceed()}
      />
    </div>
  );
}

function SystemValue({
  label,
  value,
  missing,
  showLink,
}: {
  label: string;
  value: string | null;
  missing?: string;
  showLink?: boolean;
}) {
  const { t } = useTranslation();
  return (
    <div>
      <dt className="text-label-md text-onSurfaceVariant">{label}</dt>
      <dd className="text-body-md text-onSurface">
        {value ??
          (showLink ? (
            <Link to="/account/profile" className="text-primary hover:underline">
              {missing}
            </Link>
          ) : (
            t("documents.system.notFilled")
          ))}
      </dd>
    </div>
  );
}

// Signed document: the same export behaviour as a signed note, plus the correction (a new draft).
function SignedActions({
  detail,
  onExported,
  onCorrected,
}: {
  detail: DocumentDetail;
  onExported: () => unknown | Promise<unknown>;
  onCorrected: (id: string) => void;
}) {
  const { t } = useTranslation();
  const [exporting, setExporting] = useState(false);
  const [correcting, setCorrecting] = useState(false);
  const [confirming, setConfirming] = useState(false);

  const exportPdf = async () => {
    setExporting(true);
    try {
      const { url } = await documentsApi.exportPdf(detail.id);
      if (!detail.exportedAt) await onExported();
      window.location.assign(url);
    } catch (e) {
      toast.error(e instanceof ApiError && (e.status === 409 || e.status === 503) ? e.message : t("documents.exportFailed"));
    } finally {
      setExporting(false);
    }
  };

  const correct = async () => {
    setConfirming(false);
    setCorrecting(true);
    try {
      onCorrected(await documentsApi.correct(detail.id));
    } catch (e) {
      toast.error(e instanceof ApiError && e.status === 409 ? e.message : t("documents.correctFailed"));
    } finally {
      setCorrecting(false);
    }
  };

  return (
    <DetailCard title={t("documents.signedTitle")} icon={<FileDown size={20} />}>
      {detail.exportedAt && (
        <p className="mb-1 text-body-sm text-onSurfaceVariant">
          {t("documents.exportedAt", { date: new Date(detail.exportedAt).toLocaleString("pt-BR") })}
        </p>
      )}
      <button
        type="button"
        disabled={exporting}
        onClick={() => void exportPdf()}
        className="inline-flex w-full items-center justify-center gap-2 rounded-radii-lg border border-primary px-4 py-3 text-label-lg text-primary disabled:opacity-60"
      >
        <FileDown size={16} aria-hidden />
        {detail.exportedAt ? t("documents.downloadPdf") : t("documents.exportPdf")}
      </button>
      <p className="mt-2 text-body-sm text-onSurfaceVariant">{t("documents.pdfLinkHint")}</p>
      {detail.status === "signed" && (
        <>
          <button
            type="button"
            disabled={correcting}
            onClick={() => setConfirming(true)}
            className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-radii-lg px-4 py-3 text-label-lg text-primary hover:bg-pastelLavender disabled:opacity-60"
          >
            {t("documents.correct")}
          </button>
          <p className="text-body-sm text-onSurfaceVariant">{t("documents.correctHint")}</p>
        </>
      )}
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={t("documents.correctTitle")}
        description={t("documents.correctBody")}
        confirmLabel={t("documents.correct")}
        pending={correcting}
        onConfirm={() => void correct()}
      />
    </DetailCard>
  );
}
