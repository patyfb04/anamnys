// A terminal state, deliberately not a retry affordance: the only actions that
// can change the outcome (being invited, being granted a role, having the
// account re-enabled) happen outside this app. "Log in again" would put the
// user straight back into the loop this notice exists to break.
export default function AuthErrorNotice() {
  return (
    <main className="min-h-screen flex items-center justify-center bg-surfaceContainerLow p-8">
      <div className="max-w-md text-center">
        <h1 className="text-headline-md text-onSurface">We could not sign you in</h1>
        <p className="mt-3 text-body-lg text-onSurfaceVariant">
          You signed in successfully, but this account is not set up to use Anamnys yet. Contact your
          administrator to have access granted, then try again.
        </p>
      </div>
    </main>
  );
}
