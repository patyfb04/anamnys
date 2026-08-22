"use client";

import { useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  Mic,
  FileAudio,
  FileUp,
  Users,
  ChevronRight,
  Check,
  FileEdit,
  Mic as MicSmall,
  FileText,
  Loader2,
  CalendarDays,
} from "lucide-react";
import { dashboardMockApi, ActivityIcon, ScheduleBadgeKind } from "@/api-mock/dashboard";
import Card from "@/components/ui/Card";
import Badge from "@/components/ui/Badge";

const ACTIVITY_ICONS: Record<ActivityIcon, typeof FileEdit> = {
  noteFinalized: FileEdit,
  transcriptionSaved: MicSmall,
  intakeReceived: FileText,
};

// Desktop-only styling matched 1:1 to specs/UI/Dashboard's tokens (secondary-container,
// tertiary-container, surface-variant / primary-container / surface-container-high) — not
// Badge's generic pastel BadgeTone, which has no equivalent for these exact hues.
const SCHEDULE_BADGE_STYLES: Record<ScheduleBadgeKind, string> = {
  followUp: "bg-secondaryContainer text-onSecondaryContainer",
  initialConsult: "bg-tertiaryContainer text-onTertiaryContainer",
  completed: "bg-surfaceVariant text-onSurfaceVariant",
};

const ACTIVITY_ICON_STYLES: Record<ActivityIcon, string> = {
  noteFinalized: "bg-primaryContainer text-onPrimaryContainer",
  transcriptionSaved: "bg-secondaryContainer text-onSecondaryContainer",
  intakeReceived: "bg-surfaceContainerHigh text-onSurfaceVariant",
};

// Home / Dashboard. Two distinct layouts sharing one data fetch:
//  - mobile (<md): specs/UI/mobile/Dashboard — hero live-transcription card, upload/patients
//    tiles, recent-patients activity list.
//  - desktop (md+): specs/UI/Dashboard — bento metric tiles, 3-up action cards, today's
//    schedule + a separate activity log.
// Both read from api-mock/dashboard until a real /dashboard endpoint exists.
export default function HomePage() {
  const router = useRouter();
  const { t } = useTranslation();

  const { data, isLoading, error } = useQuery({
    queryKey: ["dashboard-summary"],
    queryFn: () => dashboardMockApi.getSummary(),
  });

  if (isLoading) {
    return (
      <div className="flex justify-center py-24">
        <Loader2 className="animate-spin text-primary" size={32} />
      </div>
    );
  }

  if (error || !data) {
    return <p className="text-error text-center py-24">{t("patients.list.failedToLoad")}</p>;
  }

  return (
    <div className="p-4 pb-28 md:p-6 md:pb-6">
      {/* Greeting */}
      <div className="mb-6 md:mb-8">
        <h1 className="text-headline-md md:text-headline-lg text-onSurface">
          {t("home.greeting", { name: data.greetingName })}
        </h1>
        <p className="text-body-md md:text-body-lg text-onSurfaceVariant mt-2">
          {t("home.documentationStatus", { percent: data.documentationCompletePercent })}
        </p>
      </div>

      {/* ── Mobile ─────────────────────────────────────────────────────────── */}
      <div className="md:hidden">
        <div className="flex gap-2 mb-6">
          <div className="bg-pastelTeal text-onPastelTeal px-4 py-2 rounded-radii-full">
            <span className="text-label-md uppercase">
              {data.metrics.notesToReview} {t("dashboard.metrics.notesToReview")}
            </span>
          </div>
          <div className="bg-pastelPink text-onPastelPink px-4 py-2 rounded-radii-full">
            <span className="text-label-md uppercase">
              {data.metrics.transcribing} {t("dashboard.metrics.transcribing")}
            </span>
          </div>
        </div>

        <button
          onClick={() => router.push("/patients/new")}
          className="w-full text-left rounded-radii-lg p-6 flex flex-col justify-between gap-6 min-h-[220px] bg-gradient-to-br from-primary to-primaryContainer text-onPrimary shadow-sm mb-4"
        >
          <div>
            <span className="bg-white/20 backdrop-blur-md px-3 py-1 rounded-radii-full text-label-md mb-3 inline-block">
              {t("dashboard.metrics.transcribing").toUpperCase()}
            </span>
            <h2 className="text-headline-md">{t("home.heroTitle")}</h2>
          </div>
          <div className="flex flex-wrap gap-3 items-center">
            <span className="bg-surfaceContainerLowest text-primary px-6 py-3 rounded-radii-full text-label-lg flex items-center gap-2">
              <Mic size={18} />
              {t("home.startLiveNote")}
            </span>
            <span className="bg-white/10 text-onPrimary border border-white/20 px-5 py-3 rounded-radii-full text-label-lg">
              {t("home.resumeDraft")}
            </span>
          </div>
        </button>

        <div className="grid grid-cols-2 gap-3 mb-8">
          <Card className="flex flex-col items-center justify-center gap-2 text-center py-6">
            <div className="w-12 h-12 rounded-radii-md bg-pastelTeal text-onPastelTeal flex items-center justify-center">
              <FileAudio size={22} />
            </div>
            <span className="text-label-lg text-onSurface">{t("home.uploadAudio")}</span>
          </Card>
          <Card onClick={() => router.push("/patients")} className="flex flex-col items-center justify-center gap-2 text-center py-6">
            <div className="w-12 h-12 rounded-radii-md bg-primaryFixed text-onPrimaryFixedVariant flex items-center justify-center">
              <Users size={22} />
            </div>
            <span className="text-label-lg text-onSurface">{t("home.recentPatients")}</span>
          </Card>
        </div>

        <div className="flex items-center justify-between mb-3">
          <h3 className="text-headline-sm text-onSurface">{t("home.recentActivity")}</h3>
          <button onClick={() => router.push("/patients")} className="text-label-md text-primary uppercase">
            {t("home.viewAll")}
          </button>
        </div>
        <div className="flex flex-col gap-2">
          {data.recentPatients.map((p) => (
            <Card key={p.id}>
              <div className="flex items-center justify-between gap-3">
                <div className="flex items-center gap-3 min-w-0">
                  <div className="w-11 h-11 rounded-full bg-pastelLavender text-onPastelLavender flex items-center justify-center text-label-lg shrink-0">
                    {p.name
                      .split(" ")
                      .map((n) => n[0])
                      .slice(0, 2)
                      .join("")
                      .toUpperCase()}
                  </div>
                  <div className="min-w-0">
                    <p className="text-label-lg text-onSurface truncate">{p.name}</p>
                    <p className="text-body-md text-[12px] text-onSurfaceVariant truncate">
                      {p.lastVisitLabel} • {p.specialtyLabel}
                    </p>
                  </div>
                </div>
                <div className="flex items-center gap-3 shrink-0">
                  <Badge label={p.statusLabel} tone={p.statusTone} />
                  <ChevronRight size={20} className="text-outline" />
                </div>
              </div>
            </Card>
          ))}
        </div>
      </div>

      {/* ── Desktop ────────────────────────────────────────────────────────── */}
      <div className="hidden md:block">
        <div className="grid grid-cols-3 gap-4 mb-8">
          <div className="rounded-radii-lg p-4 flex items-center justify-between bg-errorContainer border border-onErrorContainer/25">
            <div>
              <p className="text-headline-md text-onSurface">{data.metrics.notesToReview}</p>
              <p className="text-label-md text-onSurfaceVariant uppercase tracking-wider">
                {t("dashboard.metrics.notesToReview")}
              </p>
            </div>
            <div className="w-10 h-10 rounded-full bg-errorContainer text-onErrorContainer flex items-center justify-center">
              <FileEdit size={20} />
            </div>
          </div>
          <div className="rounded-radii-lg p-4 flex items-center justify-between bg-secondaryContainer border border-onSecondaryContainer/25">
            <div>
              <p className="text-headline-md text-onSurface">{data.metrics.transcribing}</p>
              <p className="text-label-md text-onSurfaceVariant uppercase tracking-wider">
                {t("dashboard.metrics.transcribing")}
              </p>
            </div>
            <div className="w-10 h-10 rounded-full bg-secondaryContainer text-onSecondaryContainer flex items-center justify-center">
              <Mic size={20} />
            </div>
          </div>
          <div className="rounded-radii-lg p-4 flex items-center justify-between bg-primaryFixed border border-onPrimaryFixedVariant/25">
            <div>
              <p className="text-headline-md text-onSurface">{data.metrics.signaturePending}</p>
              <p className="text-label-md text-onSurfaceVariant uppercase tracking-wider">
                {t("dashboard.metrics.signaturePending")}
              </p>
            </div>
            <div className="w-10 h-10 rounded-full bg-primaryContainer text-onPrimaryContainer flex items-center justify-center">
              <Check size={20} />
            </div>
          </div>
        </div>

        <div className="grid grid-cols-3 gap-4 mb-8">
          <button
            onClick={() => router.push("/patients/new")}
            className="text-left rounded-radii-lg p-6 flex flex-col justify-between h-48 bg-primary text-onPrimary shadow-sm hover:opacity-90 transition-opacity"
          >
            <div className="w-12 h-12 rounded-full bg-onPrimary/20 flex items-center justify-center mb-4">
              <Mic size={22} />
            </div>
            <div>
              <h3 className="text-headline-sm mb-1">{t("dashboard.actions.startLiveTranscription")}</h3>
              <p className="text-body-md text-onPrimary/80">{t("dashboard.actions.startLiveTranscriptionBody")}</p>
            </div>
          </button>
          <Card className="flex flex-col justify-between h-48 hover:bg-surfaceContainer transition-colors">
            <div className="w-12 h-12 rounded-radii-md bg-primaryFixed flex items-center justify-center mb-4">
              <FileAudio size={22} className="text-primary" />
            </div>
            <div>
              <h3 className="text-headline-sm text-onSurface mb-1">{t("dashboard.actions.uploadAudio")}</h3>
              <p className="text-body-md text-onSurfaceVariant">{t("dashboard.actions.uploadAudioBody")}</p>
            </div>
          </Card>
          <Card className="flex flex-col justify-between h-48 hover:bg-surfaceContainerHigh transition-colors">
            <div className="w-12 h-12 rounded-radii-md bg-surfaceContainerHighest flex items-center justify-center mb-4">
              <FileUp size={22} className="text-primary" />
            </div>
            <div>
              <h3 className="text-headline-sm text-onSurface mb-1">{t("dashboard.actions.uploadDocument")}</h3>
              <p className="text-body-md text-onSurfaceVariant">{t("dashboard.actions.uploadDocumentBody")}</p>
            </div>
          </Card>
        </div>

        <div className="grid grid-cols-3 gap-6">
          {/* Today's Schedule */}
          <div className="col-span-2 space-y-3">
            <div className="flex items-center justify-between">
              <h3 className="text-headline-sm text-onSurface">{t("dashboard.schedule.title")}</h3>
              <button className="text-primary text-label-md flex items-center gap-1">
                <CalendarDays size={16} />
                {t("dashboard.schedule.viewCalendar")}
              </button>
            </div>
            <div className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-lg overflow-hidden shadow-sm divide-y divide-outlineVariant">
              {data.schedule.map((appt) => (
                <div
                  key={appt.id}
                  className={`p-4 flex items-center justify-between transition-colors ${
                    appt.completed ? "bg-surfaceContainerLow opacity-70" : "hover:bg-surfaceContainerLow"
                  }`}
                >
                  <div className="flex items-center gap-4">
                    <div className="text-center w-20">
                      <p className="text-label-lg text-onSurface">{appt.time}</p>
                      <p className="text-label-md text-onSurfaceVariant">{appt.period}</p>
                    </div>
                    <div>
                      <p className="text-headline-sm text-[16px] text-onSurface">{appt.patientName}</p>
                      <div className="flex items-center gap-2 mt-1">
                        <span
                          className={`px-2 py-0.5 rounded-radii-full text-label-md text-[10px] ${SCHEDULE_BADGE_STYLES[appt.badgeKind]}`}
                        >
                          {appt.badgeLabel}
                        </span>
                        {appt.subLabel && <span className="text-label-md text-onSurfaceVariant">{appt.subLabel}</span>}
                      </div>
                    </div>
                  </div>
                  <span className="w-8 h-8 rounded-full border border-outlineVariant flex items-center justify-center text-onSurfaceVariant">
                    {appt.completed ? <Check size={16} /> : <ChevronRight size={16} />}
                  </span>
                </div>
              ))}
            </div>
          </div>

          {/* Recent Activity */}
          <div className="space-y-3">
            <h3 className="text-headline-sm text-onSurface">{t("home.recentActivity")}</h3>
            <div className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-lg p-4 shadow-sm space-y-4">
              {data.recentActivity.map((item, i) => {
                const Icon = ACTIVITY_ICONS[item.icon];
                return (
                  <div key={item.id}>
                    <div className="flex gap-3">
                      <div
                        className={`w-8 h-8 rounded-full flex items-center justify-center shrink-0 ${ACTIVITY_ICON_STYLES[item.icon]}`}
                      >
                        <Icon size={16} />
                      </div>
                      <div className="min-w-0">
                        <p className="text-body-md text-onSurface">
                          {t(item.titleKey, { patientName: item.patientName })}
                        </p>
                        <p className="text-label-md text-onSurfaceVariant mt-0.5">{item.timestampLabel}</p>
                        <a href={item.href} className="inline-block mt-1.5 text-label-md text-primary hover:underline">
                          {item.actionLabel}
                        </a>
                      </div>
                    </div>
                    {i < data.recentActivity.length - 1 && <div className="h-px bg-outlineVariant w-full mt-4" />}
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
