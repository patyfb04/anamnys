import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { patientsApi } from "@anamnys/shared/api/patients";
import type { PatientDetail } from "@anamnys/shared/lib/types";
import ConfirmDialog from "../ConfirmDialog";
import { formatDate } from "../format";

interface Props {
  patient: PatientDetail;
  onChanged: () => Promise<void>;
  // Set when the create modal could not send the invitation it was asked to send.
  initialError?: string | null;
}

// "Portal do paciente" on the record page: the current access state and the action that
// moves it on. See design/specs/2026-10-03-portal-invitation-design.md §6.
export default function PortalAccessBlock({ patient, onChanged, initialError = null }: Props) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const [error, setError] = useState<string | null>(initialError);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const archived = patient.archivedAt !== null;

  const action = useMutation({
    mutationFn: (kind: "invite" | "cancel" | "remove") =>
      kind === "invite"
        ? patientsApi.invite(patient.id)
        : kind === "cancel"
          ? patientsApi.cancelInvitation(patient.id)
          : patientsApi.removePortalAccess(patient.id),
    onMutate: () => setError(null),
    onSuccess: async () => {
      setConfirmRemove(false);
      await onChanged();
    },
    onError: (e: Error) => {
      setConfirmRemove(false);
      setError(e.message);
    },
  });

  const button = (label: string, kind: "invite" | "cancel" | "remove", primary = false) => (
    <button
      type="button"
      disabled={action.isPending}
      onClick={() => (kind === "remove" ? setConfirmRemove(true) : action.mutate(kind))}
      className={
        primary
          ? "px-3 py-1.5 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90 disabled:opacity-60"
          : "px-3 py-1.5 border border-outlineVariant rounded-radii-md text-label-lg text-onSurface hover:bg-surfaceContainerLow disabled:opacity-60"
      }
    >
      {label}
    </button>
  );

  const status = (() => {
    switch (patient.portalStatus) {
      case "active":
        return t("patients.portal.active", { email: patient.portalEmail ?? "" });
      case "invited":
        return t("patients.portal.invited", {
          sent: formatDate(patient.invitationSentAt, locale),
          until: formatDate(patient.invitationExpiresAt, locale),
        });
      case "expired":
        return t("patients.portal.expired", { until: formatDate(patient.invitationExpiresAt, locale) });
      default:
        return t("patients.portal.none");
    }
  })();

  return (
    <div className="pt-3 border-t border-outlineVariant">
      <p className="text-label-md text-onSurfaceVariant">{t("patients.portal.title")}</p>
      <p className="text-body-md text-onSurface mt-1">{status}</p>
      {error && <p className="text-label-md text-error mt-1">{error}</p>}
      {!archived && (
        <div className="flex flex-wrap gap-2 mt-2">
          {patient.portalStatus === "none" && button(t("patients.portal.invite"), "invite", true)}
          {patient.portalStatus === "invited" && (
            <>
              {button(t("patients.portal.resend"), "invite")}
              {button(t("patients.portal.cancel"), "cancel")}
            </>
          )}
          {patient.portalStatus === "expired" && button(t("patients.portal.resend"), "invite", true)}
          {patient.portalStatus === "active" && button(t("patients.portal.remove"), "remove")}
        </div>
      )}
      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={(open) => !open && setConfirmRemove(false)}
        title={t("patients.portal.removeTitle")}
        description={t("patients.portal.removeBody", { name: `${patient.firstName} ${patient.lastName}` })}
        confirmLabel={t("patients.portal.remove")}
        destructive
        pending={action.isPending}
        onConfirm={() => action.mutate("remove")}
      />
    </div>
  );
}
