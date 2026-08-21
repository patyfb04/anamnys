import { BadgeTone } from "@/components/ui/Badge";

// ─── Mock-only types ─────────────────────────────────────────────────────────
// These shapes don't exist on the backend yet (no /dashboard endpoint, no scheduling domain).
// Once the real endpoint ships, swap the `dashboardMockApi` import for a real `dashboardApi`
// with the same return shape — page code doesn't need to change.

export interface DashboardMetrics {
  notesToReview: number;
  transcribing: number;
  signaturePending: number;
}

// Matches specs/UI/Dashboard's exact badge tokens (secondary-container / tertiary-container /
// surface-variant) — deliberately not Badge's generic pastel BadgeTone, which doesn't have
// equivalents for these hues.
export type ScheduleBadgeKind = "followUp" | "initialConsult" | "completed";

export interface ScheduleAppointment {
  id: string;
  time: string; // "09:00"
  period: "AM" | "PM";
  patientName: string;
  badgeLabel: string;
  badgeKind: ScheduleBadgeKind;
  subLabel?: string;
  completed?: boolean;
}

export type ActivityIcon = "noteFinalized" | "transcriptionSaved" | "intakeReceived";

export interface ActivityItem {
  id: string;
  icon: ActivityIcon;
  titleKey: string; // i18n key, interpolated with { patientName }
  patientName: string;
  timestampLabel: string;
  actionLabel: string;
  href: string;
}

export interface RecentPatientStatus {
  id: string;
  name: string;
  lastVisitLabel: string;
  specialtyLabel: string;
  statusLabel: string;
  statusTone: BadgeTone;
  pulsing?: boolean;
}

export interface DashboardSummary {
  greetingName: string;
  documentationCompletePercent: number;
  metrics: DashboardMetrics;
  schedule: ScheduleAppointment[];
  recentActivity: ActivityItem[];
  recentPatients: RecentPatientStatus[];
}

const MOCK_SUMMARY: DashboardSummary = {
  greetingName: "Dr. Chen",
  documentationCompletePercent: 85,
  metrics: {
    notesToReview: 4,
    transcribing: 2,
    signaturePending: 1,
  },
  schedule: [
    {
      id: "appt-1",
      time: "09:00",
      period: "AM",
      patientName: "Eleanor James",
      badgeLabel: "Follow-up",
      badgeKind: "followUp",
      subLabel: "PT Eval",
    },
    {
      id: "appt-2",
      time: "10:30",
      period: "AM",
      patientName: "Marcus Smith",
      badgeLabel: "Initial Consult",
      badgeKind: "initialConsult",
      subLabel: "Anxiety",
    },
    {
      id: "appt-3",
      time: "08:00",
      period: "AM",
      patientName: "Sarah Connor",
      badgeLabel: "Completed",
      badgeKind: "completed",
      completed: true,
    },
  ],
  recentActivity: [
    {
      id: "activity-1",
      icon: "noteFinalized",
      titleKey: "dashboard.activity.noteFinalized",
      patientName: "Sarah Connor",
      timestampLabel: "2 hours ago",
      actionLabel: "View Chart",
      href: "#",
    },
    {
      id: "activity-2",
      icon: "transcriptionSaved",
      titleKey: "dashboard.activity.transcriptionSaved",
      patientName: "John Doe",
      timestampLabel: "Yesterday",
      actionLabel: "Review Draft",
      href: "#",
    },
    {
      id: "activity-3",
      icon: "intakeReceived",
      titleKey: "dashboard.activity.intakeReceived",
      patientName: "Marcus Smith",
      timestampLabel: "Yesterday",
      actionLabel: "Process Form",
      href: "#",
    },
  ],
  recentPatients: [
    {
      id: "patient-1",
      name: "Jane Doe",
      lastVisitLabel: "Last visit: 10:45 AM",
      specialtyLabel: "Physical Therapy",
      statusLabel: "Draft Ready",
      statusTone: "teal",
    },
    {
      id: "patient-2",
      name: "Mark Robinson",
      lastVisitLabel: "Last visit: 09:15 AM",
      specialtyLabel: "Mental Health",
      statusLabel: "Transcribing…",
      statusTone: "pink",
      pulsing: true,
    },
    {
      id: "patient-3",
      name: "Sarah Williams",
      lastVisitLabel: "Last visit: Yesterday",
      specialtyLabel: "Consultation",
      statusLabel: "Completed",
      statusTone: "neutral",
    },
  ],
};

const MOCK_DELAY_MS = 400;

export const dashboardMockApi = {
  // Mirrors the shape a future GET /dashboard/summary would return.
  getSummary: async (): Promise<DashboardSummary> => {
    await new Promise((resolve) => setTimeout(resolve, MOCK_DELAY_MS));
    return MOCK_SUMMARY;
  },
};
