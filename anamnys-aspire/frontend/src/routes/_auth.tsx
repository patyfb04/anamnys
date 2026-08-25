import { createFileRoute, Link, Outlet, useLocation, useNavigate } from "@tanstack/react-router";
import { ShieldCheck } from "lucide-react";
import { Trans, useTranslation } from "react-i18next";

export const Route = createFileRoute("/_auth")({
  component: AuthShellLayout,
});

function AuthShellLayout() {
  const { t } = useTranslation();
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const isLogin = pathname === "/login";

  const headline = (
    <Trans
      i18nKey={
        isLogin ? "auth.login.heroHeadline" : "auth.register.heroHeadline"
      }
      components={{ accent: <span className="text-primary" /> }}
    />
  );
  const subhead = t(
    isLogin ? "auth.login.heroSubhead" : "auth.register.heroSubhead",
  );
  const headerPrompt = t(
    isLogin ? "auth.login.noAccount" : "auth.register.haveAccount",
  );
  const goTo = isLogin ? "/register" : "/login";

  return (
    <div className="min-h-screen bg-surfaceContainerLow flex flex-col">
      <header className="bg-surface border-b border-surfaceVariant px-4">
        <div className="h-16 max-w-5xl mx-auto w-full flex items-center justify-between">
          <Link to="/" className="w-[140px] h-[30px] shrink-0">
            <img
              src="/logo1.png"
              alt="Anamnys"
              width={140}
              height={30}
              className="w-full h-full object-contain"
            />
          </Link>
          <div className="flex items-center gap-4">
            <button
              onClick={() => navigate({ to: goTo })}
              className="hidden md:inline text-label-lg text-primary hover:opacity-80 transition-opacity"
            >
              {headerPrompt}
            </button>
          </div>
        </div>
      </header>

      <div className="flex-1 flex items-start justify-center p-4 pt-10 md:pt-14 pb-8">
        <div className="w-full max-w-5xl flex flex-col md:flex-row md:items-start gap-8">
          <div className="hidden md:flex md:flex-[5] flex-col">
            <h1 className="text-headline-lg text-[30px] text-onSurface mb-2.5">
              {headline}
            </h1>
            <p className="text-body-lg text-onSurfaceVariant mb-6 max-w-[360px]">
              {subhead}
            </p>
            <div className="rounded-radii-lg overflow-hidden bg-surfaceContainerHigh shadow-xl relative flex-1 min-h-[240px]">
              <img src="/sign-up.jpg" alt="" className="absolute inset-0 w-full h-full object-cover" />
              <div className="absolute inset-x-0 bottom-0 h-[55%] bg-primary/30" />
              <div className="absolute left-4 right-4 bottom-4 flex items-center gap-2 bg-white/85 border border-white/50 rounded-radii-md px-3 py-2.5">
                <ShieldCheck size={16} className="text-primary" />
                <span className="text-label-md text-onSurface normal-case">
                  {t("auth.secureBadge")}
                </span>
              </div>
            </div>
          </div>

          <div className="flex-1 md:flex-[7]">
            <div className="bg-surfaceContainerLowest rounded-radii-xl border border-surfaceContainerHighest p-4 md:p-6 shadow-xl">
              <Outlet />
              <button
                onClick={() => navigate({ to: goTo })}
                className="md:hidden mt-4 w-full text-center text-label-lg text-primary"
              >
                {headerPrompt}
              </button>
            </div>
          </div>
        </div>
      </div>

      <p className="text-center text-body-md text-onSurfaceVariant/70 pb-8">
        {t("welcome.footerCopyright")}
      </p>
    </div>
  );
}
