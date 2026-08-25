import { Fragment } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { Trans, useTranslation } from "react-i18next";
import { Check, Minus } from "lucide-react";
import clsx from "clsx";

export const Route = createFileRoute("/_marketing/pricing")({
  component: PricingPage,
});

type PlanKey = "essencial" | "profissional" | "completo";

interface PlanConfig {
  key: PlanKey;
  ctaHref: string;
  highlighted?: boolean;
}

const PLANS: PlanConfig[] = [
  { key: "essencial", ctaHref: "/register" },
  { key: "profissional", ctaHref: "/register", highlighted: true },
  { key: "completo", ctaHref: "/register" },
];

interface FeatureRow {
  name: string;
  plans: PlanKey[];
  comingSoon?: boolean;
}

interface FeatureGroup {
  title: string;
  features: FeatureRow[];
}

// Bold spans inside translated strings render as heavier weight, same gray as the
// surrounding text — matches the pattern established on the About Us page.
const boldComponent = { b: <strong className="font-bold" /> };

// Matches specs/site/Pricing.md: three real tiers (Essencial/Profissional/Completo) laid out
// as a feature-comparison table rather than repeated per-card bullet lists, since the source
// content is a 24-row x 3-column matrix. Rows whose capability is still Phase 2 per
// specs/site/Produto.md's "Em desenvolvimento" section carry an "Em breve" tag — same pattern
// as the roadmap section on the Welcome page — so Profissional/Completo can be sold today
// without overstating what's shipped.
function PricingPage() {
  const { t } = useTranslation();
  const featureGroups = t("pricing.featureGroups", { returnObjects: true }) as FeatureGroup[];
  const limits = t("pricing.limits", { returnObjects: true }) as string[];

  return (
    <div className="max-w-6xl mx-auto px-4 md:px-12 py-12 md:py-16">
      <div className="text-center mb-10 max-w-2xl mx-auto">
        <h1 className="text-headline-lg text-[28px] md:text-[32px] text-primary mb-3">{t("pricing.title")}</h1>
        <p className="text-body-lg text-onSurfaceVariant">{t("pricing.subtitle")}</p>
      </div>

      {/* Comparison table — horizontally scrollable on narrow viewports so the 3-column
          matrix never forces the page itself to scroll sideways. */}
      <div className="overflow-x-auto rounded-radii-xl border border-outlineVariant">
        <table className="w-full min-w-160 border-collapse">
          <thead>
            <tr className="bg-surfaceContainerLow">
              <th className="w-1/4" />
              {PLANS.map((plan) => (
                <th
                  key={plan.key}
                  className={clsx(
                    "px-4 py-6 text-center align-top",
                    plan.highlighted && "bg-primaryFixed/40"
                  )}
                >
                  <div className="flex flex-col items-center gap-3">
                    <span className="text-headline-sm text-[19px] text-primary">
                      {t(`pricing.plans.${plan.key}.name`)}
                    </span>
                    <span>
                      <span className="text-headline-lg text-[28px] text-onSurface">
                        {t(`pricing.plans.${plan.key}.price`)}
                      </span>
                      <span className="text-body-md text-onSurfaceVariant">{t("pricing.perMonth")}</span>
                    </span>
                    <Link
                      to={plan.ctaHref}
                      className={clsx(
                        "w-full text-center py-2.5 px-4 rounded-radii-md text-label-lg text-[14px] transition-colors",
                        plan.highlighted
                          ? "bg-primary text-onPrimary hover:opacity-90 shadow-sm"
                          : "border border-primary text-primary hover:bg-primaryFixed"
                      )}
                    >
                      {t("pricing.cta")}
                    </Link>
                  </div>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {featureGroups.map((group) => (
              <Fragment key={group.title}>
                <tr>
                  <td
                    colSpan={PLANS.length + 1}
                    className="bg-surfaceContainerLowest px-4 py-2.5 text-label-md text-onSurfaceVariant uppercase border-y border-outlineVariant"
                  >
                    {group.title}
                  </td>
                </tr>
                {group.features.map((feature) => (
                  <tr key={feature.name} className="border-b border-outlineVariant last:border-b-0">
                    <td className="px-4 py-3.5 align-top">
                      <span className="text-body-md text-onSurface">{feature.name}</span>
                      {feature.comingSoon && (
                        <span className="inline-block ml-2 border border-outline rounded-radii-full px-2 py-0.5 text-label-md text-[10px] text-onSurfaceVariant align-middle whitespace-nowrap">
                          {t("pricing.comingSoon")}
                        </span>
                      )}
                    </td>
                    {PLANS.map((plan) => (
                      <td
                        key={plan.key}
                        className={clsx(
                          "px-4 py-3.5 text-center",
                          plan.highlighted && "bg-primaryFixed/10"
                        )}
                      >
                        {feature.plans.includes(plan.key) ? (
                          <Check size={18} className="inline text-primary" />
                        ) : (
                          <Minus size={18} className="inline text-onSurfaceVariant/40" />
                        )}
                      </td>
                    ))}
                  </tr>
                ))}
              </Fragment>
            ))}
          </tbody>
        </table>
      </div>

      {/* Para quem é cada plano */}
      <div className="mt-14">
        <h2 className="text-headline-md text-[22px] text-primary text-center mb-6">
          {t("pricing.audienceTitle")}
        </h2>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-5">
          {PLANS.map((plan) => (
            <div
              key={plan.key}
              className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-lg p-5"
            >
              <h3 className="text-label-lg text-primary mb-1.5">{t(`pricing.plans.${plan.key}.name`)}</h3>
              <p className="text-body-md text-onSurfaceVariant">{t(`pricing.audience.${plan.key}`)}</p>
            </div>
          ))}
        </div>
      </div>

      {/* Trabalho em clínica */}
      <div className="mt-14 bg-surfaceContainerLowest border border-outlineVariant/30 rounded-radii-lg p-6 md:p-8">
        <h2 className="text-headline-md text-[20px] text-primary mb-3">{t("pricing.clinicTitle")}</h2>
        <p className="text-body-md text-onSurfaceVariant mb-3">
          <Trans i18nKey="pricing.clinicBody1" components={boldComponent} />
        </p>
        <p className="text-body-md text-onSurfaceVariant mb-4">{t("pricing.clinicBody2")}</p>
        <Link to="/contact-support" className="text-label-lg text-primary underline hover:opacity-80 transition-opacity">
          {t("pricing.clinicLink")}
        </Link>
      </div>

      {/* Plano anual */}
      <div className="mt-8 bg-primaryContainer rounded-radii-lg p-6 text-center">
        <h2 className="text-headline-sm text-[18px] text-onPrimary mb-1.5">{t("pricing.annualTitle")}</h2>
        <p className="text-body-md text-onPrimary/90">
          <Trans i18nKey="pricing.annualBody" components={boldComponent} />
        </p>
      </div>

      {/* Limites e condições */}
      <div className="mt-14">
        <h2 className="text-headline-md text-[20px] text-primary mb-5">{t("pricing.limitsTitle")}</h2>
        <ul className="space-y-3">
          {limits.map((_limit, i) => (
            <li key={i} className="flex gap-3">
              <span className="w-1.5 h-1.5 rounded-full bg-primary shrink-0 mt-2" />
              <p className="text-body-md text-onSurfaceVariant">
                <Trans i18nKey={`pricing.limits.${i}`} components={boldComponent} />
              </p>
            </li>
          ))}
        </ul>
      </div>

      {/* Observação sobre a cobrança */}
      <div className="mt-10 border-t border-outlineVariant pt-8">
        <h3 className="text-headline-sm text-[16px] text-primary mb-2">{t("pricing.billingTitle")}</h3>
        <p className="text-body-md text-onSurfaceVariant/80">
          <Trans i18nKey="pricing.billingBody" components={boldComponent} />
        </p>
      </div>
    </div>
  );
}
