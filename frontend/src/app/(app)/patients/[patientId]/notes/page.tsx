import PatientNotesView from "./PatientNotesView";

export default async function PatientNotesPage({ params }: PageProps<"/patients/[patientId]/notes">) {
  const { patientId } = await params;
  return <PatientNotesView patientId={patientId} />;
}
