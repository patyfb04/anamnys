import { useTranslation } from "react-i18next";
import FaqAccordion, { type FaqCategory } from "@/components/FaqAccordion";

export default function FaqPageView({
  categories,
}: {
  categories: FaqCategory[];
}) {
  const { t } = useTranslation();

  return (
    <div className="max-w-5xl mx-auto px-4 md:px-12 py-12 md:py-16">
      <div className="text-center mb-10">
        <h1 className="text-headline-lg text-[28px] md:text-[32px] text-primary mb-3">
          {t("faq.title")}
        </h1>
        <p className="text-body-md text-onSurfaceVariant max-w-2xl mx-auto">
          {t("faq.subtitle")}
        </p>
      </div>

      <FaqAccordion categories={categories} />
    </div>
  );
}
