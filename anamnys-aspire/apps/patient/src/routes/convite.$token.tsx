import { useEffect, useRef, useState } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { invitationsApi } from "@anamnys/shared/api/invitations";
import { ApiError } from "@anamnys/shared/api/client";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";

// Outside the authenticated `_app` layout on purpose: a signed-out invitee must see what
// the link is before being sent to Keycloak. See
// design/specs/2026-10-03-portal-invitation-design.md §6.
export const Route = createFileRoute("/convite/$token")({ component: InvitationPage });

type State =
  | { kind: "waiting" }
  | { kind: "accepted"; providerName: string }
  | { kind: "failed"; code: "invalid" | "email_mismatch" | "already_linked" };

const PATIENT_REALM = "anamnys-patients";

function InvitationPage() {
  const { token } = Route.useParams();
  const { t } = useTranslation();
  const { user, isLoading, login, logout } = useAuthStore();
  const [state, setState] = useState<State>({ kind: "waiting" });
  const attempted = useRef(false);

  // /api/auth/me may describe a provider session held by the same browser; only a
  // patient-portal session can accept.
  const signedIn = user?.realm === PATIENT_REALM;
  const returnUrl = window.location.pathname;

  useEffect(() => {
    if (!signedIn || attempted.current) return;
    attempted.current = true;
    invitationsApi
      .accept(token)
      .then(({ providerName }) => setState({ kind: "accepted", providerName }))
      .catch((e: unknown) => {
        const code = e instanceof ApiError ? e.code : undefined;
        setState({
          kind: "failed",
          code: code === "email_mismatch" || code === "already_linked" ? code : "invalid",
        });
      });
  }, [signedIn, token]);

  if (isLoading) return null;

  return (
    <div className="min-h-screen flex items-center justify-center bg-surfaceContainerLow px-4">
      <div className="w-full max-w-md bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-6 shadow-sm">
        <h1 className="text-headline-sm text-onSurface">{t("patientPortal.invite.title")}</h1>

        {!signedIn && (
          <>
            <p className="text-body-md text-onSurfaceVariant mt-2">{t("patientPortal.invite.signedOut")}</p>
            <div className="flex flex-col gap-2 mt-5">
              <button
                type="button"
                onClick={() => login("patient", returnUrl)}
                className="px-4 py-2.5 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90"
              >
                {t("patientPortal.invite.signIn")}
              </button>
              <button
                type="button"
                onClick={() => window.location.assign(`/auth/patient/register?returnUrl=${encodeURIComponent(returnUrl)}`)}
                className="px-4 py-2.5 border border-outlineVariant rounded-radii-md text-label-lg text-onSurface hover:bg-surfaceContainerLow"
              >
                {t("patientPortal.invite.register")}
              </button>
            </div>
          </>
        )}

        {signedIn && state.kind === "waiting" && (
          <p className="text-body-md text-onSurfaceVariant mt-2">{t("patientPortal.invite.accepting")}</p>
        )}

        {state.kind === "accepted" && (
          <>
            <p className="text-body-md text-onSurface mt-2">
              {t("patientPortal.invite.accepted", { provider: state.providerName })}
            </p>
            <Link to="/" className="inline-block mt-5 text-label-lg text-primary hover:underline">
              {t("patientPortal.invite.goHome")}
            </Link>
          </>
        )}

        {state.kind === "failed" && (
          <>
            <p className="text-body-md text-error mt-2">{t(`patientPortal.invite.errors.${state.code}`)}</p>
            {state.code === "email_mismatch" && (
              <button
                type="button"
                onClick={() => logout("patient")}
                className="mt-5 px-4 py-2.5 border border-outlineVariant rounded-radii-md text-label-lg text-onSurface hover:bg-surfaceContainerLow"
              >
                {t("patientPortal.invite.switchAccount")}
              </button>
            )}
            {state.code !== "email_mismatch" && (
              <Link to="/" className="inline-block mt-5 text-label-lg text-primary hover:underline">
                {t("patientPortal.invite.goHome")}
              </Link>
            )}
          </>
        )}
      </div>
    </div>
  );
}
