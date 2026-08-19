export default async function LiveTranscribePage({
  params,
}: PageProps<"/patients/[patientId]/live-transcribe">) {
  const { patientId } = await params;
  return (
    <div className="p-8 text-center text-onSurfaceVariant text-body-lg">
      Live transcribe for patient {patientId} — coming soon.
    </div>
  );
}
