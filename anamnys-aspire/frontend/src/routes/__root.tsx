import { Link, Outlet, createRootRoute } from '@tanstack/react-router';
import { TanStackRouterDevtools } from '@tanstack/react-router-devtools';

function NavLink({ to, children }: { to: string; children: React.ReactNode }) {
  return (
    <Link
      to={to}
      className="rounded-md px-3 py-2 text-sm font-medium text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900"
      activeProps={{ className: 'bg-slate-900 text-white hover:bg-slate-900 hover:text-white' }}
    >
      {children}
    </Link>
  );
}

function RootLayout() {
  return (
    <div className="min-h-screen bg-slate-50 text-slate-900">
      <header className="border-b border-slate-200 bg-white">
        <nav className="mx-auto flex max-w-4xl items-center gap-1 px-6 py-3">
          <span className="mr-4 font-semibold tracking-tight">Anamnys</span>
          <NavLink to="/">Home</NavLink>
          <NavLink to="/weather">Weather</NavLink>
        </nav>
      </header>
      <main className="mx-auto max-w-4xl px-6 py-10">
        <Outlet />
      </main>
      <TanStackRouterDevtools position="bottom-right" />
    </div>
  );
}

export const Route = createRootRoute({ component: RootLayout });
