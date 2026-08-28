import { createFileRoute } from "@tanstack/react-router";

// Scaffold only. The patient app will own booking, rescheduling and cancelling
// appointments; those routes land once the appointment API exists.
function Home() {
  return (
    <main className="mx-auto max-w-2xl px-6 py-16">
      <h1 className="text-3xl font-semibold tracking-tight text-slate-900">Anamnys</h1>
      <p className="mt-3 text-slate-600">
        Patient portal. Booking, rescheduling and cancelling appointments will live here.
      </p>
    </main>
  );
}

export const Route = createFileRoute("/")({ component: Home });
