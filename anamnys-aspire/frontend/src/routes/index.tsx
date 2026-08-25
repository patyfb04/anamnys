import { createFileRoute } from '@tanstack/react-router';

function Home() {
  return (
    <section className="space-y-3">
      <h1 className="text-3xl font-semibold tracking-tight">Anamnys</h1>
      <p className="text-slate-600">
        Vite + React + TanStack Router, orchestrated by Aspire. Use the Weather
        tab to verify the API proxy to the server resource.
      </p>
    </section>
  );
}

export const Route = createFileRoute('/')({ component: Home });
