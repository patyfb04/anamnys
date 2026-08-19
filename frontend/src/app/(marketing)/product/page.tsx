"use client";

import Link from "next/link";
import { Trans, useTranslation } from "react-i18next";
import {
  Mic,
  Radio,
  Keyboard,
  FileText,
  PenLine,
  Receipt,
  AlertTriangle,
  FileDown,
  Users,
  History,
  ShieldCheck,
  UploadCloud,
  TrendingUp,
  Lock,
  type LucideIcon,
} from "lucide-react";

// Bold spans inside translated strings render as heavier weight, same gray as surrounding
// text — matches the pattern established on the About Us and Pricing pages.
const boldComponent = { b: <strong className="font-bold" /> };

const AVAILABLE_ICONS: LucideIcon[] = [
  Mic,
  Radio,
  Keyboard,
  FileText,
  PenLine,
  Receipt,
  AlertTriangle,
  FileDown,
  Users,
  History,
];

const UPCOMING_GROUP_ICONS: LucideIcon[] = [
  ShieldCheck,
  UploadCloud,
  TrendingUp,
  Receipt,
  Lock,
];

interface TextItem {
  title: string;
  body: string;
}

interface UpcomingGroup {
  title: string;
  items: TextItem[];
}

// Matches specs/UI/Product's bento-style sections (hero, then stacked feature groups) built
// from the real product copy in specs/site/Produto.md. The green "Disponível agora" /
// blue "Em desenvolvimento" split is called out in the source as mandatory, not decorative —
// so it's rendered as a colored badge on each section heading, using the same secondary
// (sage green) and pastelBlue tokens already in the theme rather than inventing new colors.
export default function ProductPage() {
  const { t } = useTranslation();
  const steps = t("product.howItWorks.steps", {
    returnObjects: true,
  }) as TextItem[];
  const availableItems = t("product.available.items", {
    returnObjects: true,
  }) as TextItem[];
  const upcomingGroups = t("product.upcoming.groups", {
    returnObjects: true,
  }) as UpcomingGroup[];
  const securityItems = t("product.security.items", {
    returnObjects: true,
  }) as TextItem[];
  const audienceItems = t("product.audience.items", {
    returnObjects: true,
  }) as TextItem[];

  return (
    <div className="max-w-6xl mx-auto px-4 md:px-12 py-12 md:py-16">
      {/* Hero */}
      <div className="text-center mb-16 max-w-2xl mx-auto">
        <h1 className="text-headline-lg text-[28px] md:text-[36px] leading-tight text-primary mb-4">
          {t("product.hero.title")}
        </h1>
        <p className="text-body-lg text-onSurfaceVariant mb-7">
          {t("product.hero.subtitle")}
        </p>
        <div className="flex flex-col sm:flex-row items-center justify-center gap-3">
          <Link
            href="/register"
            className="w-full sm:w-auto text-center bg-primary text-onPrimary rounded-radii-md py-3 px-8 text-label-lg hover:opacity-90 transition-opacity shadow-sm"
          >
            {t("product.hero.ctaPrimary")}
          </Link>
          <Link
            href="/demo"
            className="w-full sm:w-auto text-center border border-outlineVariant rounded-radii-md py-3 px-8 text-label-lg text-onSurface hover:bg-surfaceContainerLow transition-colors"
          >
            {t("product.hero.ctaSecondary")}
          </Link>
        </div>
      </div>

      {/* Como funciona */}
      <div className="mb-16">
        <h2 className="text-headline-md text-[24px] text-primary text-center mb-8">
          {t("product.howItWorks.title")}
        </h2>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-5">
          {steps.map((step, i) => (
            <div
              key={step.title}
              className="bg-surfaceContainerLowest border border-outlineVariant/30 rounded-radii-lg p-6"
            >
              <span className="inline-flex items-center justify-center w-8 h-8 rounded-full bg-primaryFixed text-primary text-label-lg mb-4">
                {i + 1}
              </span>
              <h3 className="text-headline-sm text-[17px] text-onSurface mb-2">
                {step.title}
              </h3>
              <p className="text-body-md text-onSurfaceVariant">
                <Trans
                  i18nKey={`product.howItWorks.steps.${i}.body`}
                  components={boldComponent}
                />
              </p>
            </div>
          ))}
        </div>
      </div>

      {/* O que já funciona hoje */}
      <div className="mb-16">
        <div className="flex flex-col items-center text-center mb-8">
          <span className="bg-secondaryFixed text-onSecondaryFixedVariant rounded-radii-full px-3 py-1 text-label-md text-[11px] uppercase tracking-wide mb-3">
            {t("product.available.badge")}
          </span>
          <h2 className="text-headline-md text-[24px] text-primary">
            {t("product.available.title")}
          </h2>
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-5">
          {availableItems.map((item, i) => {
            const Icon = AVAILABLE_ICONS[i];
            return (
              <div
                key={item.title}
                className="bg-surfaceContainerLowest border border-outlineVariant/30 rounded-radii-lg p-5"
              >
                <div className="w-10 h-10 rounded-radii-md bg-secondaryFixed flex items-center justify-center mb-3.5">
                  <Icon size={18} className="text-onSecondaryFixedVariant" />
                </div>
                <h3 className="text-label-lg text-onSurface mb-1.5">
                  {item.title}
                </h3>
                <p className="text-body-md text-onSurfaceVariant">
                  {item.body}
                </p>
              </div>
            );
          })}
        </div>
      </div>

      {/* Em desenvolvimento */}
      <div className="mb-16">
        <div className="flex flex-col items-center text-center mb-3">
          <span className="bg-pastelBlue text-onPastelBlue rounded-radii-full px-3 py-1 text-label-md text-[11px] uppercase tracking-wide mb-3">
            {t("product.upcoming.badge")}
          </span>
          <h2 className="text-headline-md text-[24px] text-primary mb-2">
            {t("product.upcoming.title")}
          </h2>
          <p className="text-body-md text-onSurfaceVariant max-w-xl">
            {t("product.upcoming.note")}
          </p>
        </div>
        <div className="flex flex-col gap-5 mt-8">
          {upcomingGroups.map((group, gi) => {
            const GroupIcon = UPCOMING_GROUP_ICONS[gi];
            return (
              <div
                key={group.title}
                className="bg-surfaceContainerLow border border-outlineVariant/30 rounded-radii-lg p-6"
              >
                <div className="flex items-center gap-2.5 mb-4">
                  <GroupIcon size={18} className="text-primary" />
                  <h3 className="text-headline-sm text-[16px] text-onSurface">
                    {group.title}
                  </h3>
                </div>
                <div className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-4">
                  {group.items.map((item, ii) => (
                    <div key={item.title}>
                      <p className="text-label-lg text-onSurface mb-1">
                        {item.title}
                      </p>
                      <p className="text-body-md text-onSurfaceVariant">
                        <Trans
                          i18nKey={`product.upcoming.groups.${gi}.items.${ii}.body`}
                          components={boldComponent}
                        />
                      </p>
                    </div>
                  ))}
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* O diferencial */}
      <div className="mb-16 bg-primaryContainer rounded-radii-xl p-7 md:p-10">
        <h2 className="text-headline-md text-[22px] text-onPrimary mb-1.5">
          {t("product.differential.title")}
        </h2>
        <p className="text-headline-sm text-[17px] text-onPrimary/95 mb-5">
          {t("product.differential.lead")}
        </p>
        <div className="space-y-4 max-w-3xl">
          <p className="text-body-lg text-onPrimary/90">
            <Trans
              i18nKey="product.differential.p1"
              components={boldComponent}
            />
          </p>
          <p className="text-body-lg text-onPrimary/90">
            <Trans
              i18nKey="product.differential.p2"
              components={boldComponent}
            />
          </p>
          <p className="text-body-lg text-onPrimary">
            <Trans
              i18nKey="product.differential.p3"
              components={boldComponent}
            />
          </p>
          <p className="text-body-lg text-onPrimary/90">
            <Trans
              i18nKey="product.differential.p4"
              components={boldComponent}
            />
          </p>
          <p className="text-body-lg text-onPrimary/90">
            <Trans
              i18nKey="product.differential.p5"
              components={boldComponent}
            />
          </p>
        </div>
      </div>

      {/* Segurança, sem eufemismo */}
      <div className="mb-16">
        <h2 className="text-headline-md text-[24px] text-primary text-center mb-8">
          {t("product.security.title")}
        </h2>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-5 mb-6">
          {securityItems.map((item, i) => (
            <div
              key={item.title}
              className="bg-surfaceContainerLowest border border-outlineVariant/30 rounded-radii-lg p-5"
            >
              <h3 className="text-label-lg text-onSurface mb-1.5">
                {item.title}
              </h3>
              <p className="text-body-md text-onSurfaceVariant">
                <Trans
                  i18nKey={`product.security.items.${i}.body`}
                  components={boldComponent}
                />
              </p>
            </div>
          ))}
        </div>
        <div className="flex flex-col sm:flex-row items-center justify-center gap-2 sm:gap-4">
          <Link
            href="/privacy-policy"
            className="text-label-lg text-primary underline hover:opacity-80 transition-opacity"
          >
            {t("product.security.linkPrivacy")}
          </Link>
          <span className="hidden sm:inline text-onSurfaceVariant">·</span>
          <Link
            href="/terms-of-service"
            className="text-label-lg text-primary underline hover:opacity-80 transition-opacity"
          >
            {t("product.security.linkTerms")}
          </Link>
        </div>
      </div>

      {/* Para quem é */}
      <div className="mb-16">
        <h2 className="text-headline-md text-[24px] text-primary text-center mb-8">
          {t("product.audience.title")}
        </h2>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-5 mb-6">
          {audienceItems.map((item) => (
            <div
              key={item.title}
              className="bg-surfaceContainerLowest border border-outlineVariant/30 rounded-radii-lg p-5"
            >
              <h3 className="text-label-lg text-primary mb-1.5">
                {item.title}
              </h3>
              <p className="text-body-md text-onSurfaceVariant">{item.body}</p>
            </div>
          ))}
        </div>
        <p className="text-body-md text-onSurfaceVariant/80 text-center max-w-2xl mx-auto">
          <Trans
            i18nKey="product.audience.notNote"
            components={boldComponent}
          />
        </p>
      </div>

      {/* Chamada final */}
      <div className="bg-primaryContainer rounded-radii-xl p-7 md:p-12 flex flex-col items-center text-center">
        <h2 className="text-headline-md text-[22px] text-onPrimary mb-2.5">
          {t("product.finalCta.title")}
        </h2>
        <p className="text-body-lg text-onPrimary/90 mb-6">
          {t("product.finalCta.body")}
        </p>
        <div className="w-full sm:w-auto flex flex-col sm:flex-row gap-3">
          <Link
            href="/register"
            className="bg-onPrimary text-primary rounded-radii-md py-3.5 px-10 text-label-lg text-[15px] text-center hover:opacity-90 transition-opacity"
          >
            {t("product.finalCta.ctaPrimary")}
          </Link>
        </div>
      </div>
    </div>
  );
}
