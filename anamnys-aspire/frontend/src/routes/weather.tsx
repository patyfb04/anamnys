import { useState } from 'react';
import { createFileRoute } from '@tanstack/react-router';

interface WeatherForecast {
  date: string;
  temperatureC: number;
  temperatureF: number;
  summary: string;
}

async function fetchWeatherForecast(): Promise<WeatherForecast[]> {
  const response = await fetch('/api/weatherforecast');

  if (!response.ok) {
    throw new Error(`Request failed with status ${response.status}`);
  }

  return response.json();
}

function Weather() {
  const forecasts = Route.useLoaderData();
  const [useCelsius, setUseCelsius] = useState(true);

  return (
    <section className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold tracking-tight">Weather forecast</h1>
        <button
          type="button"
          onClick={() => setUseCelsius((v) => !v)}
          className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium hover:bg-slate-100"
        >
          Show {useCelsius ? '°F' : '°C'}
        </button>
      </div>

      <table className="w-full overflow-hidden rounded-lg border border-slate-200 bg-white text-left text-sm">
        <thead className="bg-slate-100 text-slate-600">
          <tr>
            <th className="px-4 py-2 font-medium">Date</th>
            <th className="px-4 py-2 font-medium">Temp.</th>
            <th className="px-4 py-2 font-medium">Summary</th>
          </tr>
        </thead>
        <tbody>
          {forecasts.map((forecast) => (
            <tr key={forecast.date} className="border-t border-slate-100">
              <td className="px-4 py-2 tabular-nums">{forecast.date}</td>
              <td className="px-4 py-2 tabular-nums">
                {useCelsius ? `${forecast.temperatureC} °C` : `${forecast.temperatureF} °F`}
              </td>
              <td className="px-4 py-2">{forecast.summary}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

export const Route = createFileRoute('/weather')({
  loader: fetchWeatherForecast,
  component: Weather,
  pendingComponent: () => <p className="text-slate-500">Loading forecast…</p>,
  errorComponent: ({ error }) => (
    <p className="rounded-md bg-red-50 px-4 py-3 text-red-700">
      Could not load the forecast: {error.message}
    </p>
  ),
});
