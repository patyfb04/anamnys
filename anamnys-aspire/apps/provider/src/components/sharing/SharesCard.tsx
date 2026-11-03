import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { Share2 } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import { sharesApi } from "@anamnys/shared/api/sharing";
import type { DisclosureAuthorizationItem, ShareLinkItem } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import DetailCard from "@/components/patients/detail/DetailCard";
import ShareFlowModal, { type RecordedAuthorization } from "./ShareFlowModal";

const TERM_URL_LIFETIME_MS = 60_000;

const when = (iso: string) => new Date(iso).toLocaleString("pt-BR");
// The form sends a plain date, which the server stores as midnight UTC: showing it in the
// practice's time zone would move it to the day before.
const authorizedDay = (iso: string) => new Date(iso).toLocaleDateString("pt-BR", { timeZone: "UTC" });

function LinkRow({ link, onRevoke }: { link: ShareLinkItem; onRevoke?: () => void }) {
  const { t } = useTranslation();
  const active = link.status === "active";
  return (
    <li className="flex flex-col gap-1 rounded-radii-md border border-outlineVariant p-3">
      <div className="flex items-center justify-between gap-2">
        <span
          className={`rounded-radii-full px-2 py-0.5 text-label-md ${active ? "bg-pastelLavender text-onSurface" : "bg-surfaceContainerLow text-onSurfaceVariant"}`}
        >
          {t(`sharing.status.${link.status}`)}
        </span>
        {active && onRevoke && (
          <button type="button" onClick={onRevoke} className="text-label-lg text-error hover:underline">
            {t("sharing.list.revoke")}
          </button>
        )}
      </div>
      <p className="text-body-sm text-onSurfaceVariant">{t("sharing.list.expires", { date: when(link.expiresAt) })}</p>
      <p className="text-body-sm text-onSurfaceVariant">
        {link.viewCap === null
          ? t("sharing.list.views", { count: link.viewsUsed })
          : t("sharing.list.viewsCapped", { used: link.viewsUsed, cap: link.viewCap })}
        {" · "}
        {t(link.downloadEnabled ? "sharing.list.downloadOn" : "sharing.list.downloadOff")}
      </p>
      <details className="text-body-sm text-onSurfaceVariant">
        <summary className="cursor-pointer text-primary hover:underline">
          {t("sharing.list.accesses", { count: link.accessCount })}
        </summary>
        {link.accessCount > link.accesses.length && (
          <p className="mt-1">{t("sharing.list.accessesNewest", { shown: link.accesses.length, total: link.accessCount })}</p>
        )}
        {link.accesses.length === 0 ? (
          <p className="mt-1">{t("sharing.list.noAccesses")}</p>
        ) : (
          <ul className="mt-1 flex flex-col gap-1">
            {link.accesses.map((access, i) => (
              <li key={`${access.at}-${i}`}>
                <span className="text-onSurface">{when(access.at)}</span> · {t(`sharing.outcome.${access.outcome}`)}
                {access.userAgent && (
                  <span className="block truncate" title={access.userAgent}>
                    {access.userAgent}
                  </span>
                )}
              </li>
            ))}
          </ul>
        )}
      </details>
    </li>
  );
}

function AuthorizationBlock({
  authorization: a,
  canShare,
  onNewLink,
  onRevoke,
}: {
  authorization: DisclosureAuthorizationItem;
  canShare: boolean;
  onNewLink: () => void;
  onRevoke: (linkId: string) => void;
}) {
  const { t } = useTranslation();
  const [openingTerm, setOpeningTerm] = useState(false);

  const openTerm = async () => {
    // Opened before the request: a tab opened after an await is stopped as a popup.
    const tab = window.open("", "_blank");
    if (!tab) {
      // Blocked as a popup: don't make an audited opening that shows nothing.
      toast.error(t("sharing.list.termFailed"));
      return;
    }
    setOpeningTerm(true);
    try {
      const url = URL.createObjectURL(await sharesApi.openTerm(a.id));
      if (tab) tab.location.href = url;
      else toast.error(t("sharing.list.termFailed"));
      // The new tab has loaded it by then, and the URL dies with this page anyway.
      window.setTimeout(() => URL.revokeObjectURL(url), TERM_URL_LIFETIME_MS);
    } catch {
      tab?.close();
      toast.error(t("sharing.list.termFailed"));
    } finally {
      setOpeningTerm(false);
    }
  };

  return (
    <li className="flex flex-col gap-2">
      <div>
        <p className="wrap-break-word text-label-lg text-onSurface">
          {a.recipientName} · {a.recipientRole}
        </p>
        <p className="wrap-break-word text-body-sm text-onSurfaceVariant">{a.purpose}</p>
        <p className="text-body-sm text-onSurfaceVariant">
          {t("sharing.list.authorized", { date: authorizedDay(a.authorizedAt), via: t(`sharing.obtainedVia.${a.obtainedVia}`) })}
          {a.hasTerm && (
            <>
              {" · "}
              <button type="button" disabled={openingTerm} onClick={() => void openTerm()} className="text-primary hover:underline disabled:opacity-60">
                {t("sharing.list.openTerm")}
              </button>
            </>
          )}
        </p>
      </div>
      {a.links.length > 0 && (
        <ul className="flex flex-col gap-2">
          {a.links.map((link) => (
            <LinkRow key={link.id} link={link} onRevoke={() => onRevoke(link.id)} />
          ))}
        </ul>
      )}
      {canShare && (
        <button type="button" onClick={onNewLink} className="self-start text-label-lg text-primary hover:underline">
          {t("sharing.list.newLink")}
        </button>
      )}
    </li>
  );
}

// Document page card: who this document was shared with, and the way to share it again. Which
// documents can be shared is the server's answer, so the rules are not repeated here.
export default function SharesCard({ documentId }: { documentId: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const queryKey = ["document-shares", documentId];
  const shares = useQuery({ queryKey, queryFn: () => sharesApi.list(documentId) });
  const [flow, setFlow] = useState<{ authorization: RecordedAuthorization | null } | null>(null);
  const [revoking, setRevoking] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  const refresh = () => queryClient.invalidateQueries({ queryKey });

  const revoke = async () => {
    if (!revoking) return;
    setPending(true);
    try {
      await sharesApi.revoke(revoking);
      toast.success(t("sharing.list.revoked"));
      setRevoking(null);
      await refresh();
    } catch {
      toast.error(t("sharing.list.revokeFailed"));
    } finally {
      setPending(false);
    }
  };

  const data = shares.data;
  const hasLinks = data?.authorizations.some((a) => a.links.length > 0) ?? false;

  return (
    <DetailCard title={t("sharing.title")} icon={<Share2 size={20} />}>
      {shares.isPending ? null : shares.isError || !data ? (
        <p className="text-body-md text-error">
          {shares.error instanceof ApiError && shares.error.status === 404 ? t("documents.notFound") : t("sharing.list.loadFailed")}
        </p>
      ) : (
        <div className="flex flex-col gap-4">
          {data.canShare ? (
            <button
              type="button"
              onClick={() => setFlow({ authorization: null })}
              className="inline-flex w-full items-center justify-center gap-2 rounded-radii-lg bg-primary px-4 py-3 text-label-lg text-onPrimary"
            >
              <Share2 size={16} aria-hidden />
              {t("sharing.share")}
            </button>
          ) : (
            data.blockedReason && <p className="text-body-md text-onSurfaceVariant">{t(`sharing.blocked.${data.blockedReason}`)}</p>
          )}

          {data.supersededBy && hasLinks && (
            <p className="rounded-radii-lg bg-surfaceContainerLow p-3 text-body-sm text-onSurface">
              {t("sharing.list.newerVersion")}{" "}
              <Link to="/documents/$documentId" params={{ documentId: data.supersededBy }} className="text-primary hover:underline">
                {t("documents.openReplacement")}
              </Link>
            </p>
          )}

          {data.authorizations.length === 0 ? (
            data.canShare && <p className="text-body-md text-onSurfaceVariant">{t("sharing.list.empty")}</p>
          ) : (
            <ul className="flex flex-col gap-4">
              {data.authorizations.map((a) => (
                <AuthorizationBlock
                  key={a.id}
                  authorization={a}
                  canShare={data.canShare}
                  onNewLink={() => setFlow({ authorization: { id: a.id, recipientName: a.recipientName } })}
                  onRevoke={setRevoking}
                />
              ))}
            </ul>
          )}
        </div>
      )}

      {flow && (
        <ShareFlowModal documentId={documentId} authorization={flow.authorization} onChanged={() => void refresh()} onClose={() => setFlow(null)} />
      )}
      <ConfirmDialog
        open={revoking !== null}
        onOpenChange={(open) => {
          if (!open) setRevoking(null);
        }}
        title={t("sharing.list.revokeTitle")}
        description={t("sharing.list.revokeBody")}
        confirmLabel={t("sharing.list.revoke")}
        destructive
        pending={pending}
        onConfirm={() => void revoke()}
      />
    </DetailCard>
  );
}
