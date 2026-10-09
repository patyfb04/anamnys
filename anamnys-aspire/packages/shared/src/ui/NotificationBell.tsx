import { useEffect, useRef, useState } from "react";
import { Bell } from "lucide-react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { notificationsApi } from "@anamnys/shared/api/notifications";
import type { ProviderNotification } from "@anamnys/shared/lib/types";

const TIME_ZONE = "America/Sao_Paulo";

// en-CA formats as YYYY-MM-DD: the calendar route's `date` search param.
const dayFormat = new Intl.DateTimeFormat("en-CA", { timeZone: TIME_ZONE });
const whenFormat = new Intl.DateTimeFormat("pt-BR", {
  timeZone: TIME_ZONE,
  day: "2-digit",
  month: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});

// Bell with an unread badge and a dropdown of the provider's notifications.
export default function NotificationBell({ className }: { className?: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);

  const { data } = useQuery({
    queryKey: ["notifications"],
    queryFn: () => notificationsApi.list(1),
    refetchInterval: 60_000,
  });
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["notifications"] });
  const markRead = useMutation({ mutationFn: notificationsApi.markRead, onSuccess: invalidate });
  const markAll = useMutation({ mutationFn: notificationsApi.markAllRead, onSuccess: invalidate });

  useEffect(() => {
    if (!open) return;
    const onPointer = (e: MouseEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onPointer);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onPointer);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const unread = data?.unreadCount ?? 0;
  const items = data?.items ?? [];

  const label = (n: ProviderNotification) =>
    t(n.kind === "appointment_confirmed" ? "notifications.confirmed" : "notifications.autoCancelled", {
      patient: n.patientName,
      when: whenFormat.format(new Date(n.startsAt)),
    });

  return (
    <div ref={root} className="md:relative">
      <button
        type="button"
        className={`relative ${className ?? ""}`}
        aria-label={t("notifications.open")}
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        <Bell size={22} />
        {unread > 0 && (
          <span className="absolute top-0 right-0 min-w-4 h-4 px-1 rounded-full bg-error text-onError text-[10px] leading-4 text-center">
            {unread > 9 ? "9+" : unread}
          </span>
        )}
      </button>

      {open && (
        <div className="absolute z-50 left-0 right-0 top-16 md:left-auto md:top-full md:mt-2 md:w-80 max-h-96 overflow-y-auto bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md shadow-lg">
          <div className="px-4 py-3 border-b border-outlineVariant text-title-sm text-onSurface">
            {t("notifications.title")}
          </div>
          {items.length === 0 ? (
            <p className="px-4 py-6 text-center text-body-md text-onSurfaceVariant">{t("notifications.empty")}</p>
          ) : (
            <ul>
              {items.map((n) => (
                <li key={n.id} className="border-b border-outlineVariant last:border-b-0">
                  <Link
                    to="/calendar"
                    search={{ view: "day", date: dayFormat.format(new Date(n.startsAt)) }}
                    className={`block px-4 py-3 text-body-md text-onSurface hover:bg-surfaceContainerLow ${
                      n.readAt ? "" : "font-bold"
                    }`}
                    onClick={() => {
                      if (!n.readAt) markRead.mutate(n.id);
                      setOpen(false);
                    }}
                  >
                    {label(n)}
                  </Link>
                </li>
              ))}
            </ul>
          )}
          <div className="px-4 py-3 border-t border-outlineVariant">
            <button
              type="button"
              className="text-label-md text-primary disabled:opacity-50"
              disabled={unread === 0 || markAll.isPending}
              onClick={() => markAll.mutate()}
            >
              {t("notifications.markAll")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
