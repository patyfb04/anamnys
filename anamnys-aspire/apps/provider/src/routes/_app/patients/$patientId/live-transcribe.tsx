import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/_app/patients/$patientId/live-transcribe")({
  component: LiveTranscribePage,
});

function LiveTranscribePage() {
  const { patientId } = Route.useParams();
  return (
    <div className="p-8 text-center text-onSurfaceVariant text-body-lg">
      Live transcribe for patient {patientId} — coming soon.
    </div>
  );
}
