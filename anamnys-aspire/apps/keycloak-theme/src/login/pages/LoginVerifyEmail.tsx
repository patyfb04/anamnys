import { Template } from '../Template';
import type { KcContext } from '../KcContext';
import type { I18n } from '../i18n';

type Props = {
  kcContext: Extract<KcContext, { pageId: 'login-verify-email.ftl' }>;
  i18n: I18n;
};

// Shown right after registration completes (and again on any future login attempt
// with an unverified account): Keycloak's own required-action page for
// VERIFY_EMAIL, restyled with the same Anamnys shell as Register.tsx instead of
// keycloakify's stock Template. No form here — Keycloak already sent the email by
// the time this page renders, so this is purely "check your inbox," plus a resend
// link (url.loginAction, Keycloak's own resend-verification action).
export default function LoginVerifyEmail({ kcContext, i18n }: Props) {
  const { url, user } = kcContext;
  const { msg, msgStr } = i18n;

  return (
    <Template
      kcContext={kcContext}
      i18n={i18n}
      headline={msg('emailVerifyTitle')}
      subhead={msgStr('emailVerifyInstruction1', user?.email ?? '')}
    >
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{msg('emailVerifyTitle')}</h2>

      <p className="text-body-md text-onSurfaceVariant mt-3.5">{msg('emailVerifyInstruction1', user?.email ?? '')}</p>

      <p className="text-body-md text-onSurfaceVariant mt-3.5">
        {msg('emailVerifyInstruction2')}{' '}
        <a href={url.loginAction} className="text-primary underline">
          {msg('doClickHere')}
        </a>{' '}
        {msg('emailVerifyInstruction3')}
      </p>

      <a href={url.loginUrl} className="block text-center mt-4.5 text-label-lg text-primary">
        {msg('backToLogin')}
      </a>
    </Template>
  );
}
