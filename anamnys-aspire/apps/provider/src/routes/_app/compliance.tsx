import { useState, type ReactNode } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { complianceApi } from "@anamnys/shared/api/compliance";
import { documentsApi } from "@anamnys/shared/api/documents";
import type { RetentionItem } from "@anamnys/shared/lib/types";
import DiscardDialog from "@/components/compliance/DiscardDialog";
import DisposalTermsList from "@/components/compliance/DisposalTermsList";
import { Pager } from "@/components/compliance/Pager";
import { usePaged } from "@/components/compliance/usePaged";

export const Route = createFileRoute("/_app/compliance")({
  component: CompliancePage,
});

const instantFormat = new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", timeZone: "America/Sao_Paulo" });
// yyyy-MM-dd calendar dates are shown as written, without a time zone shift.
const day = (iso: string) => iso.split("-").reverse().join("/");

// One block of the panel, five rows per page.
function Block<T>({ title, empty, items, row }: { title: string; empty: string; items: T[]; row: (item: T) => ReactNode }) {
  const paged = usePaged(items);
  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-title-md text-onSurface">{title}</h2>
      {items.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{empty}</p>
      ) : (
        <>
          <ul className="flex flex-col divide-y divide-outlineVariant rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest">
            {paged.shown.map(row)}
          </ul>
          <Pager paged={paged} />
        </>
      )}
    </section>
  );
}

const rowClass = "flex flex-wrap items-center justify-between gap-2 px-4 py-3 text-body-md text-onSurface";

// Retention and compliance panel: lists ordered by date, no severity ranking.
function CompliancePage() {
  const { t } = useTranslation();
  const [discarding, setDiscarding] = useState<RetentionItem | null>(null);
  const { data, isPending, isError } = useQuery({ queryKey: ["compliance"], queryFn: complianceApi.summary });
  const kinds = useQuery({ queryKey: ["document-kinds"], queryFn: documentsApi.kinds });

  const kindTitle = (kind: string) => kinds.data?.find((k) => k.kind === kind)?.title ?? kind;
  const elementLabel = (kind: string, code: string) =>
    kinds.data?.find((k) => k.kind === kind)?.elements.find((e) => e.code === code)?.label ?? code;

  return (
    <div className="flex flex-col gap-8 p-4 md:p-8">
      <div>
        <h1 className="text-headline-lg text-onSurface">{t("compliance.title")}</h1>
        <p className="text-body-md text-onSurfaceVariant">{t("compliance.subtitle")}</p>
      </div>

      {isPending ? null : isError ? (
        <p role="alert" className="text-body-md text-error">
          {t("compliance.loadFailed")}
        </p>
      ) : (
        <>
          <Block title={t("compliance.unsigned.title")} empty={t("compliance.unsigned.empty")} items={data.unsignedNotes} row={(n) => (
              <li key={n.noteId} className={rowClass}>
                <span>
                  <Link to="/patients/$patientId" params={{ patientId: n.patientId }} className="hover:underline">
                    {n.patientName}
                  </Link>
                  {" · "}
                  {t("compliance.unsigned.item", { date: instantFormat.format(new Date(n.createdAt)), days: n.ageDays })}
                </span>
                <Link to="/notes/$noteId" params={{ noteId: n.noteId }} className="text-label-lg text-primary hover:underline">
                  {t("compliance.unsigned.open")}
                </Link>
              </li>
            )} />

          <Block title={t("compliance.retention.title")} empty={t("compliance.retention.empty")} items={data.retention} row={(r) => (
              <li key={r.patientId} className={rowClass}>
                <span className="flex flex-col">
                  <Link to="/patients/$patientId" params={{ patientId: r.patientId }} className="self-start hover:underline">
                    {r.patientName}
                  </Link>
                  <span className="text-body-sm text-onSurfaceVariant">
                    {t("compliance.retention.item", { last: day(r.lastRecordAt), until: day(r.retentionUntil) })}
                    {" · "}
                    {r.expired ? t("compliance.retention.expired") : t("compliance.retention.within")}
                  </span>
                </span>
                {r.expired && (
                  <span className="flex flex-col items-end gap-1">
                    <button
                      type="button"
                      disabled={r.hasFutureAppointment}
                      onClick={() => setDiscarding(r)}
                      className="rounded-radii-md border border-outlineVariant px-3 py-1.5 text-label-lg text-onSurface hover:bg-pastelLavender disabled:opacity-50 disabled:hover:bg-transparent"
                    >
                      {t("compliance.retention.discard")}
                    </button>
                    {r.hasFutureAppointment && (
                      <span className="text-body-sm text-onSurfaceVariant">{t("compliance.retention.futureAppointment")}</span>
                    )}
                  </span>
                )}
              </li>
            )} />

          <Block title={t("compliance.documents.title")} empty={t("compliance.documents.empty")} items={data.documents} row={(d) => (
              <li key={d.documentId} className="flex flex-col items-start gap-2 px-4 py-3 text-body-md text-onSurface">
                <span className="flex flex-col">
                  <span>
                    <Link to="/patients/$patientId" params={{ patientId: d.patientId }} className="hover:underline">
                      {d.patientName}
                    </Link>
                    {" · "}
                    {kindTitle(d.kind)} · {t(`compliance.documents.status.${d.status}`)} · {instantFormat.format(new Date(d.createdAt))}
                  </span>
                  <span className="text-body-sm text-onSurfaceVariant">
                    {d.missing.length === 0
                      ? t("compliance.documents.onlySignature")
                      : t("compliance.documents.missing", { items: d.missing.map((c) => elementLabel(d.kind, c)).join(", ") })}
                  </span>
                </span>
                <Link to="/documents/$documentId" params={{ documentId: d.documentId }} className="text-label-lg text-primary hover:underline">
                  {t("compliance.documents.open")}
                </Link>
              </li>
            )} />
        </>
      )}

      <DisposalTermsList />
      {discarding && <DiscardDialog item={discarding} onClose={() => setDiscarding(null)} />}
    </div>
  );
}
