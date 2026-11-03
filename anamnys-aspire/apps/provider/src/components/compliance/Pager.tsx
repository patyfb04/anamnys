import { useTranslation } from "react-i18next";
import type { usePaged } from "./usePaged";

const pagerButton =
  "rounded-radii-md px-3 py-1 text-label-md text-primary hover:bg-pastelLavender disabled:cursor-default disabled:text-onSurfaceVariant disabled:hover:bg-transparent";

export function Pager({ paged }: { paged: ReturnType<typeof usePaged<unknown>> }) {
  const { t } = useTranslation();
  if (paged.pages <= 1) return null;
  return (
    <div className="flex flex-wrap items-center gap-2">
      <button type="button" className={pagerButton} disabled={paged.page === 0} onClick={() => paged.setPage(paged.page - 1)}>
        {t("compliance.pager.previous")}
      </button>
      <span className="text-body-sm text-onSurfaceVariant" aria-live="polite">
        {t("compliance.pager.range", { from: paged.first + 1, to: paged.first + paged.shown.length, total: paged.total })}
      </span>
      <button type="button" className={pagerButton} disabled={paged.page >= paged.pages - 1} onClick={() => paged.setPage(paged.page + 1)}>
        {t("compliance.pager.next")}
      </button>
    </div>
  );
}
