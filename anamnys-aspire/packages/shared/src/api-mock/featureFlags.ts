// Mock-only feature flags gating sidenav items whose underlying feature isn't built yet
// (no route, no backend endpoint). Everything defaults to `true` so the full nav is visible
// today; flip an entry to `false` to stage a rollout ahead of that feature actually shipping.
// Once real flags exist server-side, swap `featureFlagsMockApi` for a real client with the
// same return shape.

export type DashboardNavFeature =
  | "calendar"
  | "liveSession"
  | "noteEditor"
  | "documentIntake"
  | "analytics"
  | "secureSharing"
  | "complianceVault"
  | "billing";

export type FeatureFlags = Record<DashboardNavFeature, boolean>;

const MOCK_FLAGS: FeatureFlags = {
  calendar: true,
  liveSession: true,
  noteEditor: true,
  documentIntake: true,
  analytics: true,
  secureSharing: true,
  complianceVault: true,
  billing: true,
};

const MOCK_DELAY_MS = 150;

export const featureFlagsMockApi = {
  getFlags: async (): Promise<FeatureFlags> => {
    await new Promise((resolve) => setTimeout(resolve, MOCK_DELAY_MS));
    return MOCK_FLAGS;
  },
};
