import { ArrowUp } from "lucide-react";
import { useTranslation } from "react-i18next";

export const ANALYSIS_TOP_ID = "analysis-top";

// At the bottom of each open card of the Análises page: back to the patient's name and the anchors.
export default function BackToTop() {
  const { t } = useTranslation();

  return (
    <div className="mt-3 flex justify-end">
      <button
        type="button"
        onClick={() => document.getElementById(ANALYSIS_TOP_ID)?.scrollIntoView({ behavior: "smooth", block: "start" })}
        className="inline-flex items-center gap-1 rounded-radii-full px-2 py-1 text-label-sm text-onSurfaceVariant hover:bg-pastelLavender"
      >
        <ArrowUp size={14} />
        {t("analysis.backToTop")}
      </button>
    </div>
  );
}
