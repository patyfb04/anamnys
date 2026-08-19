"use client";

import Image from "next/image";
import Link from "next/link";
import { Trans, useTranslation } from "react-i18next";
import {
  Mic,
  FileText,
  Receipt,
  BadgeCheck,
  Shield,
  User,
  History,
  TrendingUp,
  Calendar,
  Video,
  UploadCloud,
  ClipboardCheck,
  PlayCircle,
  LucideIcon,
} from "lucide-react";
import Button from "@/components/ui/Button";
import FadeInOnScroll from "@/components/FadeInOnScroll";
import MarketingHeader from "@/components/MarketingHeader";
import MarketingFooter from "@/components/MarketingFooter";

interface FeatureDef {
  Icon: LucideIcon;
  iconBg: string;
  iconColor: string;
  key: string;
}

const FEATURES: FeatureDef[] = [
  {
    Icon: Mic,
    iconBg: "bg-primaryFixed",
    iconColor: "text-primary",
    key: "speechToNote",
  },
  {
    Icon: FileText,
    iconBg: "bg-secondaryFixed",
    iconColor: "text-secondary",
    key: "structuredNotes",
  },
  {
    Icon: Receipt,
    iconBg: "bg-tertiaryFixed",
    iconColor: "text-tertiary",
    key: "billing",
  },
  {
    Icon: BadgeCheck,
    iconBg: "bg-primaryFixed",
    iconColor: "text-primary",
    key: "auditTrail",
  },
  {
    Icon: Shield,
    iconBg: "bg-secondaryFixed",
    iconColor: "text-secondary",
    key: "secure",
  },
  {
    Icon: User,
    iconBg: "bg-tertiaryFixed",
    iconColor: "text-tertiary",
    key: "therapy",
  },
];

// Phase 2 roadmap items — not shipped yet, rendered with a "Coming Soon" tag.
const ROADMAP_FEATURES: FeatureDef[] = [
  {
    Icon: History,
    iconBg: "bg-primaryFixed",
    iconColor: "text-primary",
    key: "longitudinal",
  },
  {
    Icon: TrendingUp,
    iconBg: "bg-secondaryFixed",
    iconColor: "text-secondary",
    key: "evidenceLinking",
  },
  {
    Icon: Calendar,
    iconBg: "bg-tertiaryFixed",
    iconColor: "text-tertiary",
    key: "scheduling",
  },
  {
    Icon: Video,
    iconBg: "bg-primaryFixed",
    iconColor: "text-primary",
    key: "videoSessions",
  },
  {
    Icon: UploadCloud,
    iconBg: "bg-secondaryFixed",
    iconColor: "text-secondary",
    key: "documentIntake",
  },
  {
    Icon: ClipboardCheck,
    iconBg: "bg-tertiaryFixed",
    iconColor: "text-tertiary",
    key: "compliance",
  },
  {
    Icon: Shield,
    iconBg: "bg-primaryFixed",
    iconColor: "text-primary",
    key: "secureSharing",
  },
];

function FeatureCard({
  feature,
  delayMs,
  namespace = "features",
  comingSoon = false,
}: {
  feature: FeatureDef;
  delayMs: number;
  namespace?: "features" | "roadmapFeatures";
  comingSoon?: boolean;
}) {
  const { t } = useTranslation();
  const { Icon } = feature;

  return (
    <FadeInOnScroll
      delayMs={delayMs}
      className="w-full md:basis-[31%] md:grow md:max-w-[31%]"
    >
      <div className="h-full bg-surfaceContainerLow border border-outlineVariant rounded-radii-lg p-6 flex flex-col items-center text-center transition-all duration-200 hover:border-primary hover:shadow-lg hover:shadow-primary/20 hover:-translate-y-1 group">
        {comingSoon && (
          <span className="border border-outline rounded-radii-full px-2.5 py-0.5 mb-3 text-label-md text-[10px] text-onSurfaceVariant">
            {t("welcome.comingSoon")}
          </span>
        )}
        <div
          className={`w-14 h-14 rounded-radii-md flex items-center justify-center mb-3.5 transition-transform duration-200 group-hover:scale-110 ${feature.iconBg}`}
        >
          <Icon size={24} className={feature.iconColor} />
        </div>
        <h3 className="text-headline-sm text-[17px] text-primary mb-2">
          {t(`welcome.${namespace}.${feature.key}.title`)}
        </h3>
        <p className="text-body-md text-onSurfaceVariant leading-5">
          {t(`welcome.${namespace}.${feature.key}.body`)}
        </p>
      </div>
    </FadeInOnScroll>
  );
}

export default function WelcomePage() {
  const { t } = useTranslation();

  return (
    <div className="min-h-screen bg-surface">
      <MarketingHeader />

      <main className="px-4 md:px-12 pb-12 flex flex-col items-center">
        <div className="max-w-7xl w-full flex flex-col items-center">
          <h1 className="text-headline-lg text-[30px] leading-[38px] md:text-[48px] md:leading-[56px] text-primary text-center mb-3 md:mt-10 md:mb-5 max-w-[780px]">
            <Trans
              i18nKey="welcome.headline"
              components={{ accent: <span className="text-primary" /> }}
            />
          </h1>

          <FadeInOnScroll>
            <p className="text-body-lg text-onSurfaceVariant text-center mb-6 max-w-[640px]">
              {t("welcome.subhead")}
            </p>
          </FadeInOnScroll>

          <div className="w-full sm:w-auto flex flex-col sm:flex-row sm:items-center gap-3 mb-8">
            <Link href="/register" className="sm:min-w-[220px]">
              <Button title={t("welcome.startTrial")} rounded="md" />
            </Link>
            <Link
              href="/demo"
              className="w-full sm:w-auto flex items-center justify-center gap-2 bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md py-3.5 px-7 hover:bg-surfaceContainerLow transition-colors"
            >
              <PlayCircle size={20} className="text-onSurface" />
              <span className="text-label-lg text-[15px] text-onSurface">
                {t("welcome.watchDemo")}
              </span>
            </Link>
          </div>

          <FadeInOnScroll className="w-full max-w-[1000px] mb-14">
            <div className="bg-white/70 border border-black/10 rounded-radii-xl p-2 shadow-2xl">
              <div className="rounded-radii-lg overflow-hidden aspect-[16/10] bg-surfaceContainerHigh relative">
                <Image
                  src="/home-image.jpg"
                  alt=""
                  fill
                  className="object-cover"
                />
              </div>
            </div>
            <div className="w-40 bg-surfaceContainerLowest rounded-radii-md p-3 shadow-lg -mt-5 ml-5 relative">
              <div className="flex items-center gap-1.5 mb-2">
                <Mic size={16} className="text-tertiary" />
                <span className="text-label-md text-onSurface normal-case">
                  {t("welcome.liveSyncing")}
                </span>
              </div>
              <div className="h-1 rounded-full bg-tertiaryFixed overflow-hidden">
                <div className="h-1 w-[65%] bg-tertiary" />
              </div>
            </div>
          </FadeInOnScroll>

          <FadeInOnScroll className="flex flex-col items-center mb-6">
            <h2 className="text-headline-md text-[24px] text-primary text-center mb-2">
              {t("welcome.sectionTitle")}
            </h2>
            <p className="text-body-lg text-onSurfaceVariant text-center mb-6">
              {t("welcome.sectionSubtitle")}
            </p>
          </FadeInOnScroll>

          <div className="w-full max-w-[1100px] flex flex-col md:flex-row md:flex-wrap md:justify-center gap-4 md:gap-8 mb-4">
            {FEATURES.map((f, i) => (
              <FeatureCard key={f.key} feature={f} delayMs={i * 100} />
            ))}
          </div>

          <FadeInOnScroll className="flex flex-col items-center mb-6 mt-8">
            <h2 className="text-headline-md text-[24px] text-primary text-center mb-2">
              {t("welcome.roadmapTitle")}
            </h2>
            <p className="text-body-lg text-onSurfaceVariant text-center mb-6">
              {t("welcome.roadmapSubtitle")}
            </p>
          </FadeInOnScroll>

          <div className="w-full max-w-[1100px] flex flex-col md:flex-row md:flex-wrap md:justify-center gap-4 md:gap-8 mb-8">
            {ROADMAP_FEATURES.map((f, i) => (
              <FeatureCard
                key={f.key}
                feature={f}
                delayMs={i * 100}
                namespace="roadmapFeatures"
                comingSoon
              />
            ))}
          </div>

          <FadeInOnScroll className="w-full max-w-[800px] bg-primaryContainer rounded-radii-xl p-7 md:p-12 flex flex-col items-center text-center my-8">
            <h2 className="text-headline-md text-[22px] text-onPrimary mb-2.5">
              {t("welcome.ctaTitle")}
            </h2>
            <p className="text-body-lg text-onPrimary/90 mb-5">
              {t("welcome.ctaBody")}
            </p>
            <div className="w-full sm:w-auto flex flex-col sm:flex-row gap-3">
              <Link
                href="/register"
                className="bg-onPrimary text-primary rounded-radii-md py-3.5 px-10 text-label-lg text-[15px] text-center hover:opacity-90 transition-opacity"
              >
                {t("welcome.ctaGetStarted")}
              </Link>
            </div>
          </FadeInOnScroll>
        </div>
      </main>

      <MarketingFooter />
    </div>
  );
}
