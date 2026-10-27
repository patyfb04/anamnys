import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { externalDocumentsApi } from "@anamnys/shared/api/externalDocuments";
import type { ExternalDocumentItem, ExternalScanStatus } from "@anamnys/shared/lib/types";
import Badge, { type BadgeTone } from "@anamnys/shared/ui/Badge";
import Card from "@anamnys/shared/ui/Card";
import { toast } from "@anamnys/shared/ui/Toaster";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import { formatDate } from "@/components/patients/format";
import DocumentReadStatus from "./DocumentReadStatus";
import { formatBytes } from "./fileCheck";

const TONE: Record<ExternalScanStatus, BadgeTone> = { pending: "blue", clean: "mint", infected: "pink", failed: "neutral" };

// One received document. Open (clean only) asks the server for a one-hour link and navigates to it;
// the file name is display-only and never goes into a URL. Delete needs confirmation and is hidden
// for archived patients (readOnly). With showPatient the patient name links to the record.
export default function ExternalDocumentRow({
  doc,
  readOnly,
  showPatient = false,
  variant = "row",
}: {
  doc: ExternalDocumentItem;
  readOnly: boolean;
  showPatient?: boolean;
  variant?: "row" | "card";
}) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);
  const available = doc.scanStatus === "clean";

  // The tab is opened synchronously on click (so popup blockers allow it) and pointed at the
  // one-hour link once the server answers; the app itself stays where it is.
  const open = useMutation({
    mutationFn: async () => {
      const w = window.open("", "_blank");
      if (w) w.opener = null;
      try {
        return { w, ...(await externalDocumentsApi.open(doc.id)) };
      } catch (e) {
        w?.close();
        throw e;
      }
    },
    onSuccess: ({ w, url }) => {
      if (!/^https?:\/\//i.test(url)) {
        w?.close();
        toast.error(t("externalDocuments.openFailed"));
      } else if (w) w.location.href = url;
      else toast.error(t("externalDocuments.popupBlocked"));
    },
    onError: (e) => {
      toast.error(e instanceof ApiError && e.code === "not_available" ? t("externalDocuments.errors.not_available") : t("externalDocuments.openFailed"));
      void queryClient.invalidateQueries({ queryKey: ["external-documents"] });
    },
  });
  const remove = useMutation({
    mutationFn: () => externalDocumentsApi.remove(doc.id),
    onSuccess: async () => {
      setConfirming(false);
      toast.success(t("externalDocuments.deleted"));
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["external-documents"] }),
        // The document's facts go with it.
        queryClient.invalidateQueries({ queryKey: ["declared-facts", doc.patientId] }),
      ]);
    },
    onError: (e) => {
      setConfirming(false);
      toast.error(e instanceof ApiError && e.code === "archived" ? t("externalDocuments.errors.archived") : t("externalDocuments.deleteFailed"));
    },
  });

  const meta = [
    t(`externalDocuments.kinds.${doc.kind}`),
    doc.documentDate ? t("externalDocuments.documentDate", { date: formatDate(doc.documentDate, i18n.language) }) : null,
    t("externalDocuments.uploadedOn", { date: formatDate(doc.uploadedAt, i18n.language) }),
    formatBytes(doc.byteSize),
  ].filter(Boolean);

  if (variant === "card") {
    return (
      <li>
        <Card onClick={available && !open.isPending ? () => open.mutate() : undefined}>
          <div className="flex items-center justify-between gap-2 mb-2">
            <span className="text-label-md text-onSurfaceVariant normal-case">
              {[t("externalDocuments.uploadedOn", { date: formatDate(doc.uploadedAt, i18n.language) }), doc.documentDate ? t("externalDocuments.documentDate", { date: formatDate(doc.documentDate, i18n.language) }) : null].filter(Boolean).join(" · ")}
            </span>
            <Badge label={t(`externalDocuments.status.${doc.scanStatus}`)} tone={TONE[doc.scanStatus]} />
          </div>
          <p className="text-headline-sm text-[16px] text-onSurface mb-1 truncate">{t(`externalDocuments.kinds.${doc.kind}`)}</p>
          <p className="text-body-md text-onSurfaceVariant truncate">{doc.fileName} · {formatBytes(doc.byteSize)}</p>
          {doc.description && <p className="text-body-sm text-onSurface break-words mt-1">{doc.description}</p>}
          <div className="mt-1">
            <DocumentReadStatus doc={doc} readOnly={readOnly} onOpenOriginal={() => open.mutate()} />
          </div>
          <div className="mt-1 flex flex-wrap items-center justify-between gap-2">
            {showPatient && doc.patientName ? (
              <Link
                to="/patients/$patientId"
                params={{ patientId: doc.patientId }}
                onClick={(e) => e.stopPropagation()}
                className="text-body-md text-onSurfaceVariant hover:underline truncate"
              >
                {doc.patientName}
              </Link>
            ) : <span />}
            {!readOnly && (
              <button
                type="button"
                onClick={(e) => {
                  e.stopPropagation();
                  setConfirming(true);
                }}
                className="text-label-lg text-primary hover:underline"
              >
                {t("externalDocuments.delete")}
              </button>
            )}
          </div>
        </Card>
        <ConfirmDialog
          open={confirming}
          onOpenChange={setConfirming}
          title={t("externalDocuments.deleteTitle")}
          description={t("externalDocuments.deleteConfirm", { name: doc.fileName })}
          confirmLabel={t("externalDocuments.delete")}
          destructive
          pending={remove.isPending}
          onConfirm={() => remove.mutate()}
        />
      </li>
    );
  }

  return (
    <li className="flex flex-wrap items-center justify-between gap-2 py-3">
      <div className="min-w-0 flex-1">
        {available ? (
          <button
            type="button"
            onClick={() => open.mutate()}
            disabled={open.isPending}
            className="max-w-full truncate text-left text-label-lg text-primary hover:underline disabled:opacity-60"
          >
            {doc.fileName}
          </button>
        ) : (
          <p className="truncate text-label-lg text-onSurface">{doc.fileName}</p>
        )}
        <p className="text-body-sm text-onSurfaceVariant">{meta.join(" · ")}</p>
        {showPatient && doc.patientName && (
          <p className="text-body-sm text-onSurfaceVariant">
            <Link to="/patients/$patientId" params={{ patientId: doc.patientId }} className="hover:underline">
              {doc.patientName}
            </Link>
          </p>
        )}
        {doc.description && <p className="text-body-sm text-onSurface break-words">{doc.description}</p>}
        <div className="mt-1">
          <DocumentReadStatus doc={doc} readOnly={readOnly} onOpenOriginal={() => open.mutate()} />
        </div>
      </div>
      <div className="flex items-center gap-3">
        <Badge label={t(`externalDocuments.status.${doc.scanStatus}`)} tone={TONE[doc.scanStatus]} />
        {!readOnly && (
          <button type="button" onClick={() => setConfirming(true)} className="text-label-lg text-primary hover:underline">
            {t("externalDocuments.delete")}
          </button>
        )}
      </div>
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={t("externalDocuments.deleteTitle")}
        description={t("externalDocuments.deleteConfirm", { name: doc.fileName })}
        confirmLabel={t("externalDocuments.delete")}
        destructive
        pending={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
    </li>
  );
}
