"use client";

import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Search, ChevronRight, UserPlus, Loader2 } from "lucide-react";
import { patientsApi } from "@/api/patients";
import { Patient } from "@/lib/types";
import TextField from "@/components/ui/TextField";
import Card from "@/components/ui/Card";
import Avatar from "@/components/ui/Avatar";

const RECENT_WINDOW_MS = 48 * 60 * 60 * 1000;

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString();
}

function PatientRow({ patient, avatarSize, onClick }: { patient: Patient; avatarSize: number; onClick: () => void }) {
  const { t } = useTranslation();
  return (
    <Card onClick={onClick}>
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3 min-w-0">
          <Avatar name={`${patient.firstName} ${patient.lastName}`} size={avatarSize} />
          <div className="min-w-0">
            <p className="text-label-lg text-[15px] text-onSurface truncate">
              {patient.firstName} {patient.lastName}
            </p>
            <p className="text-body-md text-[12px] text-onSurfaceVariant mt-0.5">
              {patient.lastVisit
                ? t("patients.list.lastVisit", { date: formatDate(patient.lastVisit) })
                : t("patients.list.dob", { date: formatDate(patient.dateOfBirth) })}
            </p>
          </div>
        </div>
        <ChevronRight size={22} className="text-outline shrink-0" />
      </div>
    </Card>
  );
}

// Searchable list of patients that navigates into a patient's profile on selection.
export default function PatientListPage() {
  const router = useRouter();
  const { t } = useTranslation();
  const [search, setSearch] = useState("");

  const { data, isLoading, error } = useQuery({
    queryKey: ["patients"],
    queryFn: () => patientsApi.list(1, 50),
  });

  // Captured once via useState's lazy initializer (React's blessed pattern for a one-time
  // impure read like Date.now()) rather than called directly during render, which the
  // react-hooks/purity rule disallows.
  const [now] = useState(() => Date.now());

  const patients = data?.items ?? [];
  const filtered = patients.filter((p) => {
    const full = `${p.firstName} ${p.lastName}`.toLowerCase();
    return full.includes(search.toLowerCase());
  });

  const recent = useMemo(() => {
    return filtered
      .filter((p) => p.lastVisit && now - new Date(p.lastVisit).getTime() < RECENT_WINDOW_MS)
      .slice(0, 3);
  }, [filtered, now]);

  return (
    <div className="relative min-h-full">
      <div className="p-4 pb-28 max-w-2xl mx-auto">
        <TextField
          icon={Search}
          placeholder={t("patients.list.searchPlaceholder")}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          containerClassName="mt-4 mb-6"
          pill
        />

        {isLoading ? (
          <div className="flex justify-center py-12">
            <Loader2 className="animate-spin text-primary" size={32} />
          </div>
        ) : error ? (
          <p className="text-error text-center py-12">{t("patients.list.failedToLoad")}</p>
        ) : (
          <>
            {recent.length > 0 && (
              <div className="mb-6">
                <div className="flex items-center justify-between mb-2">
                  <h2 className="text-headline-sm text-onSurfaceVariant">{t("patients.list.recent")}</h2>
                  <span className="text-label-md text-primary uppercase tracking-wide">{t("patients.list.last48h")}</span>
                </div>
                <div className="flex flex-col gap-2">
                  {recent.map((p) => (
                    <PatientRow key={p.id} patient={p} avatarSize={40} onClick={() => router.push(`/patients/${p.id}`)} />
                  ))}
                </div>
              </div>
            )}

            <h2 className="text-headline-sm text-onSurfaceVariant mb-3">{t("patients.list.allPatients")}</h2>
            {filtered.length === 0 ? (
              <p className="text-body-md text-onSurfaceVariant text-center mt-8">{t("patients.list.noPatientsFound")}</p>
            ) : (
              <div className="flex flex-col gap-2">
                {filtered.map((p) => (
                  <PatientRow key={p.id} patient={p} avatarSize={44} onClick={() => router.push(`/patients/${p.id}`)} />
                ))}
              </div>
            )}
          </>
        )}
      </div>

      <button
        onClick={() => router.push("/patients/new")}
        className="fixed bottom-24 right-5 w-15 h-15 rounded-radii-full bg-primary shadow-lg shadow-primary/35 flex items-center justify-center"
      >
        <UserPlus size={26} className="text-onPrimary" />
      </button>
    </div>
  );
}
