import { useState } from 'react';
import { Mail, Lock } from 'lucide-react';
import Button from '@anamnys/shared/ui/Button';
import TextField from '@anamnys/shared/ui/TextField';
import { Template } from '../Template';
import type { KcContext } from '../KcContext';
import type { I18n } from '../i18n';

type Props = {
  kcContext: Extract<KcContext, { pageId: 'login.ftl' }>;
  i18n: I18n;
};

// A native form post to Keycloak's url.loginAction, not a controlled React form calling
// an API — Keycloak owns the credential exchange, this page only collects input. Every
// input needs a real `name` attribute for the post to carry anything.
export default function Login({ kcContext, i18n }: Props) {
  const { url, realm, login, messagesPerField } = kcContext;
  const { msg, msgStr } = i18n;
  const [submitting, setSubmitting] = useState(false);

  const usernameError = messagesPerField.existsError('username', 'password')
    ? messagesPerField.getFirstError('username', 'password')
    : null;

  return (
    <Template kcContext={kcContext} i18n={i18n} headline={msg('loginAccountTitle')} subhead={msgStr('doLogIn')}>
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{msg('loginAccountTitle')}</h2>

      {usernameError && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{usernameError}</p>
        </div>
      )}

      <form action={url.loginAction} method="post" onSubmit={() => setSubmitting(true)}>
        <TextField
          name="username"
          label={msgStr('email')}
          icon={Mail}
          type="email"
          autoCapitalize="none"
          autoComplete="username"
          defaultValue={login.username ?? ''}
          containerClassName="mb-3.5"
        />
        <TextField
          name="password"
          label={msgStr('password')}
          icon={Lock}
          secureToggle
          autoComplete="current-password"
          containerClassName="mb-3.5"
        />

        <Button type="submit" title={msgStr('doLogIn')} loading={submitting} rounded="md" className="mt-1" />
      </form>

      {realm.resetPasswordAllowed && (
        <a href={url.loginResetCredentialsUrl} className="block text-center mt-4.5 text-label-lg text-primary">
          {msg('doForgotPassword')}
        </a>
      )}
    </Template>
  );
}
