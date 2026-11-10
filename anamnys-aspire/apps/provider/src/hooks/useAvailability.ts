import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { availabilityApi, blocksApi } from "@anamnys/shared/api/availability";
import type { AvailabilityResponse, BlockRequest } from "@anamnys/shared/lib/types";

export const AVAILABILITY_KEY = "availability";
export const HOLIDAY_SETTINGS_KEY = "holiday-settings";
export const OVERLAY_KEY = "calendar-overlay";
export const BLOCKS_KEY = "blocks";

const UPCOMING_DAYS = 90;

export function useAvailabilityQuery() {
  return useQuery({ queryKey: [AVAILABILITY_KEY], queryFn: availabilityApi.get });
}

// Windows, holidays and blocks over the visible local dates (both included).
export function useCalendarOverlay(days: string[]) {
  const from = days[0];
  const to = days[days.length - 1];
  return useQuery({
    queryKey: [OVERLAY_KEY, from, to],
    queryFn: () => availabilityApi.overlay(from, to),
    placeholderData: keepPreviousData,
  });
}

// Blocks overlapping now .. now + 90 days, soonest first.
export function useUpcomingBlocks() {
  return useQuery({
    queryKey: [BLOCKS_KEY, "upcoming"],
    queryFn: async () => {
      const from = new Date();
      const to = new Date(from.getTime() + UPCOMING_DAYS * 24 * 60 * 60 * 1000);
      const items = await blocksApi.list(from, to);
      return [...items].sort((a, b) => a.startsAt.localeCompare(b.startsAt));
    },
  });
}

export function useBlockMutations() {
  const queryClient = useQueryClient();
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: [BLOCKS_KEY] }),
      queryClient.invalidateQueries({ queryKey: [OVERLAY_KEY] }),
      queryClient.invalidateQueries({ queryKey: ["appointments"] }),
      queryClient.invalidateQueries({ queryKey: ["attendance"] }),
      queryClient.invalidateQueries({ queryKey: ["timeline"] }),
    ]);
  return {
    preview: useMutation({ mutationFn: (r: BlockRequest) => blocksApi.preview(r) }),
    create: useMutation({ mutationFn: (r: BlockRequest) => blocksApi.create(r), onSuccess: refresh }),
    remove: useMutation({ mutationFn: (id: string) => blocksApi.remove(id), onSuccess: refresh }),
  };
}

// Whether the provider has saved hours that apply on a date, so that empty windows mean "não atendo" rather than
// "nothing defined". Only the current and upcoming versions are known here; a date before the current version counts
// as covered only when the overlay returned windows for it.
export function hasHoursOn(date: string, availability: AvailabilityResponse | undefined): boolean {
  if (!availability) return false;
  const versions = [...(availability.current ? [availability.current] : []), ...availability.upcoming];
  return versions.some((v) => v.effectiveFrom <= date);
}
