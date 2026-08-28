import { createFileRoute } from "@tanstack/react-router";
import PatientNotesView from "@/components/PatientNotesView";

export const Route = createFileRoute("/_app/patients/$patientId/notes")({
  component: PatientNotesPage,
});

function PatientNotesPage() {
  const { patientId } = Route.useParams();
  return <PatientNotesView patientId={patientId} />;
}
