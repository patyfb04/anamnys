import { useState } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { Trash2 } from "lucide-react";
import type { BlockItem } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import { useBlockMutations, useUpcomingBlocks } from "@/hooks/useAvailability";
import { PRACTICE_ZONE, addDays, formatDate, formatTime, zonedParts } from "./calendarTime";
import { BAND_STYLE } from "./overlay";

// "10/01/2026 a 20/01/2026" for an all-day block (the stored end is the next local midnight); "10/01/2026 14:00–16:00"
// or "10/01/2026 14:00 a 11/01/2026 09:00" for a timed one.
function period(t: TFunction, b: BlockItem): string {
  const start = new Date(b.startsAt);
  const end = new Date(b.endsAt);
  const startDate = zonedParts(start, PRACTICE_ZONE).date;
  if (b.allDay) {
    const last = addDays(zonedParts(end, PRACTICE_ZONE).date, -1);
    return last === startDate ? formatDate(startDate) : t("blocks.range", { from: formatDate(startDate), to: formatDate(last) });
  }
  const endDate = zonedParts(end, PRACTICE_ZONE).date;
  if (endDate === startDate) return `${formatDate(startDate)} ${formatTime(start, PRACTICE_ZONE)}–${formatTime(end, PRACTICE_ZONE)}`;
  return t("blocks.range", {
    from: `${formatDate(startDate)} ${formatTime(start, PRACTICE_ZONE)}`,
    to: `${formatDate(endDate)} ${formatTime(end, PRACTICE_ZONE)}`,
  });
}

// "Próximos bloqueios" (next 90 days) with delete. Deleting a block never touches appointments.
export default function UpcomingBlocksPanel() {
  const { t } = useTranslation();
  const query = useUpcomingBlocks();
  const { remove } = useBlockMutations();
  const [confirming, setConfirming] = useState<string | null>(null);
  const items = query.data ?? [];

  const confirmDelete = async (id: string) => {
    try {
      await remove.mutateAsync(id);
      toast.success(t("blocks.deleted"));
    } catch {
      toast.error(t("blocks.deleteFailed"));
    } finally {
      setConfirming(null);
    }
  };

  return (
    <section className="mt-6 rounded-radii-xl border border-outlineVariant bg-surfaceContainerLowest p-5">
      <h2 className="mb-3 text-title-md text-onSurface">{t("blocks.upcomingTitle")}</h2>
      {query.isError && <p className="text-body-md text-error">{t("blocks.loadFailed")}</p>}
      {query.isSuccess && items.length === 0 && <p className="text-body-md text-onSurfaceVariant">{t("blocks.upcomingEmpty")}</p>}
      {items.length > 0 && (
        <ul className="flex flex-col gap-2">
          {items.map((b) => (
            <li key={b.id} className={`flex flex-wrap items-center justify-between gap-2 rounded-radii-md px-3 py-2 ${BAND_STYLE[b.kind]}`}>
              <div className="flex min-w-0 flex-col">
                <span className="text-label-lg">{t(`blocks.kind.${b.kind}`)}{b.reason ? `: ${b.reason}` : ""}</span>
                <span className="text-body-sm">{period(t, b)}</span>
              </div>
              {confirming === b.id ? (
                <div className="flex items-center gap-2">
                  <span className="text-body-sm">{t("blocks.deleteConfirm")}</span>
                  <button type="button" disabled={remove.isPending} onClick={() => confirmDelete(b.id)}
                    className="rounded-radii-md bg-error px-3 py-1 text-label-md text-onError disabled:opacity-60">
                    {t("blocks.delete")}
                  </button>
                  <button type="button" onClick={() => setConfirming(null)} className="rounded-radii-md px-3 py-1 text-label-md hover:bg-pastelLavender">
                    {t("common.cancel")}
                  </button>
                </div>
              ) : (
                <button type="button" onClick={() => setConfirming(b.id)} aria-label={t("blocks.delete")} title={t("blocks.delete")}
                  className="rounded-radii-full p-1.5 hover:bg-pastelLavender">
                  <Trash2 size={16} />
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
