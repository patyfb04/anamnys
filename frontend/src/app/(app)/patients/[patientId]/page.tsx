import PatientDetailView from "./PatientDetailView";

export default async function PatientDetailPage({ params }: PageProps<"/patients/[patientId]">) {
  const { patientId } = await params;
  return <PatientDetailView patientId={patientId} />;
}
