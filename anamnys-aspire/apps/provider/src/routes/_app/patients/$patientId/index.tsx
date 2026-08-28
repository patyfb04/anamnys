import { createFileRoute } from "@tanstack/react-router";
import PatientDetailView from "@/components/PatientDetailView";

export const Route = createFileRoute("/_app/patients/$patientId/")({
  component: PatientDetailPage,
});

function PatientDetailPage() {
  const { patientId } = Route.useParams();
  return <PatientDetailView patientId={patientId} />;
}
