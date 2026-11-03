import { useEffect, useState, type FormEvent } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { Download } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import { publicSharesApi } from "@anamnys/shared/api/sharing";
import type { SharedDocument } from "@anamnys/shared/lib/types";

// Public page outside the authenticated `_app` layout, for a recipient with no account. Plain
// state and direct calls on purpose: useQuery and useMutation would keep the token and the
// password in the query cache. A dead link and a wrong password show the same message, so the
// page never says which it was. See design/specs/2026-11-01-protected-sharing-design.md.
export const Route = createFileRoute("/s/$token")({ component: SharePage });

function SharePage() {
  const { token } = Route.useParams();
  const { t } = useTranslation();
  const [linkState, setLinkState] = useState<"checking" | "ready" | "unavailable">("checking");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [failure, setFailure] = useState<"denied" | "transient" | null>(null);
  const [shared, setShared] = useState<SharedDocument | null>(null);

  useEffect(() => {
    let active = true;
    publicSharesApi
      .check(token)
      .then(() => active && setLinkState("ready"))
      // Only a 404 is an answer about the link. The password form decides after anything else.
      .catch((e) => active && setLinkState(e instanceof ApiError && e.status === 404 ? "unavailable" : "ready"));
    return () => {
      active = false;
    };
  }, [token]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const value = password.trim();
    // An empty password would count as a wrong attempt on a live link.
    if (!value || submitting) return;
    setPassword("");
    setFailure(null);
    setSubmitting(true);
    try {
      setShared(await publicSharesApi.open(token, value));
    } catch (e) {
      // 403 and 404 are the server's answers about the link or the password, and read the same.
      // Anything else (429, 5xx, no network) says nothing about either, so it can say "try again".
      const denied = e instanceof ApiError && (e.status === 403 || e.status === 404);
      setFailure(denied ? "denied" : "transient");
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <>
      {/* The page HTML does not pass through the API, so its headers do not cover it. React hoists these to <head>. */}
      <meta name="robots" content="noindex, nofollow" />
      <meta name="referrer" content="no-referrer" />
      {shared ? (
        <SharedDocumentViewer shared={shared} />
      ) : (
        <div className="min-h-screen flex items-center justify-center bg-surfaceContainerLow px-4">
          <div className="w-full max-w-md bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-6 shadow-sm">
            <h1 className="text-headline-sm text-onSurface">{t("sharing.public.title")}</h1>

            {linkState === "checking" && (
              <p role="status" className="text-body-md text-onSurfaceVariant mt-2">
                {t("sharing.public.checking")}
              </p>
            )}

            {(failure === "denied" || linkState === "unavailable") && (
              <div role="alert" className="mt-2">
                <p className="text-body-md text-error">{t("sharing.public.failed")}</p>
                <p className="text-body-sm text-onSurfaceVariant mt-1">{t("sharing.public.failedHint")}</p>
              </div>
            )}

            {failure === "transient" && (
              <p role="alert" className="text-body-md text-error mt-2">
                {t("sharing.public.transient")}
              </p>
            )}

            {linkState === "ready" && (
              <form onSubmit={(e) => void submit(e)} className="mt-3 flex flex-col gap-4">
                <p className="text-body-md text-onSurfaceVariant">{t("sharing.public.intro")}</p>
                <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
                  {t("sharing.public.password")}
                  <input
                    type="password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="off"
                    spellCheck={false}
                    autoFocus
                    className="w-full bg-surfaceContainerLowest border border-outlineVariant focus:border-primary rounded-radii-md px-3 py-2 text-body-md text-onSurface outline-none"
                  />
                </label>
                <button
                  type="submit"
                  disabled={submitting || !password.trim()}
                  className="px-4 py-2.5 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90 disabled:opacity-60"
                >
                  {submitting ? t("sharing.public.opening") : t("sharing.public.open")}
                </button>
              </form>
            )}
          </div>
        </div>
      )}
    </>
  );
}

// The blob stays in memory for the life of the page: opening again would use another view.
function SharedDocumentViewer({ shared }: { shared: SharedDocument }) {
  const { t } = useTranslation();
  const [url, setUrl] = useState<string | null>(null);

  useEffect(() => {
    const objectUrl = URL.createObjectURL(shared.blob);
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [shared.blob]);

  return (
    <div className="flex min-h-screen flex-col gap-3 bg-surfaceContainerLow p-4">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <h1 className="text-headline-sm text-onSurface">{t("sharing.public.title")}</h1>
          <p className="text-body-sm text-onSurfaceVariant">{t("sharing.public.viewerHint")}</p>
        </div>
        {url && shared.downloadName && (
          <a
            href={url}
            download={shared.downloadName}
            className="inline-flex items-center gap-2 rounded-radii-md border border-primary px-4 py-2 text-label-lg text-primary hover:bg-pastelLavender"
          >
            <Download size={16} aria-hidden />
            {t("sharing.public.download")}
          </a>
        )}
      </header>
      {url && (
        <object
          data={shared.downloadName ? url : `${url}#toolbar=0`}
          type="application/pdf"
          aria-label={t("sharing.public.title")}
          className="min-h-[80vh] w-full flex-1 rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest"
        >
          <p className="p-4 text-body-md text-onSurfaceVariant">
            {t(shared.downloadName ? "sharing.public.noViewerDownload" : "sharing.public.noViewer")}
          </p>
        </object>
      )}
    </div>
  );
}
