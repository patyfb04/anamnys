import { useState } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { confirmationsApi } from "@anamnys/shared/api/confirmations";
import { ApiError } from "@anamnys/shared/api/client";
import { sessionWhen } from "@/components/portal/sessionFormat";

// Public page outside the authenticated `_app` layout: the e-mailed token is the only
// credential. Opening the link never confirms; only the button does.
// See design/specs/2026-10-07-appointment-notifications-design.md §5.
export const Route = createFileRoute("/confirmar/$token")({ component: ConfirmationPage });

function ConfirmationPage() {
  const { token } = Route.useParams();
  const { t, i18n } = useTranslation();
  const [confirmed, setConfirmed] = useState(false);
  const [invalid, setInvalid] = useState(false);
  const [failed, setFailed] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const view = useQuery({
    queryKey: ["confirmation", token],
    queryFn: () => confirmationsApi.view(token),
    retry: false,
  });

  const confirm = async () => {
    setSubmitting(true);
    setFailed(false);
    try {
      await confirmationsApi.confirm(token);
      setConfirmed(true);
    } catch (e) {
      if (e instanceof ApiError && e.status === 410) setInvalid(true);
      else setFailed(true);
    } finally {
      setSubmitting(false);
    }
  };

  const data = view.data;
  const status = confirmed ? "confirmed" : invalid ? "invalid" : data?.status;
  const details = data && data.startsAt && data.endsAt && data.providerName ? data : null;
  const when = details
    ? sessionWhen(
        {
          id: "",
          startsAt: details.startsAt!,
          endsAt: details.endsAt!,
          timezone: details.timezone ?? "",
          modality: details.modality ?? "online",
          status: "scheduled",
        },
        i18n.language,
        t(`patientPortal.sessions.modality.${details.modality === "presencial" ? "presencial" : "online"}`),
      )
    : null;

  return (
    <div className="min-h-screen flex items-center justify-center bg-surfaceContainerLow px-4">
      <div className="w-full max-w-md bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-6 shadow-sm">
        <h1 className="text-headline-sm text-onSurface">{t("patientPortal.confirmation.title")}</h1>

        {view.isPending && (
          <p role="status" className="text-body-md text-onSurfaceVariant mt-2">
            {t("patientPortal.confirmation.loading")}
          </p>
        )}

        {/* The GET answers 200 "invalid" for an unknown token, so an error here is transient. */}
        {view.isError && (
          <>
            <p role="alert" className="text-body-md text-error mt-2">{t("patientPortal.confirmation.failed")}</p>
            <button
              type="button"
              onClick={() => view.refetch()}
              disabled={view.isFetching}
              className="mt-4 px-4 py-2.5 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90 disabled:opacity-60"
            >
              {t("patientPortal.confirmation.retry")}
            </button>
          </>
        )}

        {status === "invalid" && !view.isError && (
          <p role="status" className="text-body-md text-error mt-2">{t("patientPortal.confirmation.invalid")}</p>
        )}

        {details && status !== "invalid" && (
          <div className="mt-2">
            <p className="text-body-md text-onSurface">
              {t("patientPortal.confirmation.details", { provider: details.providerName })}
            </p>
            <p className="text-body-md text-onSurfaceVariant mt-1">{when}</p>
          </div>
        )}

        {status === "confirmed" && (
          <p role="status" className="text-body-md text-onSurface mt-4">{t("patientPortal.confirmation.confirmed")}</p>
        )}

        {status === "pending" && (
          <>
            {failed && <p role="alert" className="text-body-md text-error mt-4">{t("patientPortal.confirmation.failed")}</p>}
            <button
              type="button"
              onClick={confirm}
              disabled={submitting}
              className="mt-5 px-4 py-2.5 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90 disabled:opacity-60"
            >
              {t("patientPortal.confirmation.confirm")}
            </button>
          </>
        )}
      </div>
    </div>
  );
}
