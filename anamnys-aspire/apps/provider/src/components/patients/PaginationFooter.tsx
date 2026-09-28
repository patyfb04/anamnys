import { useTranslation } from "react-i18next";
import { ChevronLeft, ChevronRight } from "lucide-react";

interface Props {
  page: number;
  pageSize: number;
  totalCount: number;
  onPage: (page: number) => void;
}

export default function PaginationFooter({ page, pageSize, totalCount, onPage }: Props) {
  const { t } = useTranslation();
  const from = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, totalCount);
  const buttonClass =
    "p-1.5 rounded-radii-md text-onSurfaceVariant hover:bg-surfaceContainer disabled:opacity-40 disabled:hover:bg-transparent transition-colors";

  return (
    <div className="flex items-center justify-between px-4 py-3 border-t border-outlineVariant">
      <p className="text-body-md text-onSurfaceVariant">{t("patients.list.showing", { from, to, total: totalCount })}</p>
      <div className="flex gap-1">
        <button
          type="button"
          className={buttonClass}
          disabled={page <= 1}
          onClick={() => onPage(page - 1)}
          aria-label={t("patients.list.previousPage")}
        >
          <ChevronLeft size={20} />
        </button>
        <button
          type="button"
          className={buttonClass}
          disabled={page * pageSize >= totalCount}
          onClick={() => onPage(page + 1)}
          aria-label={t("patients.list.nextPage")}
        >
          <ChevronRight size={20} />
        </button>
      </div>
    </div>
  );
}
