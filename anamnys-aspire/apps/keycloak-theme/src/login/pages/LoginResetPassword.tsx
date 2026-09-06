import { useState } from 'react';
import { Mail } from 'lucide-react';
import Button from '@anamnys/shared/ui/Button';
import TextField from '@anamnys/shared/ui/TextField';
import { Template } from '../Template';
import type { KcContext } from '../KcContext';
import type { I18n } from '../i18n';

type Props = {
  kcContext: Extract<KcContext, { pageId: 'login-reset-password.ftl' }>;
  i18n: I18n;
};

export default function LoginResetPassword({ kcContext, i18n }: Props) {
  const { url, messagesPerField } = kcContext;
  const { msg, msgStr } = i18n;
  const [submitting, setSubmitting] = useState(false);

  const error = messagesPerField.existsError('username') ? messagesPerField.get('username') : null;

  return (
    <Template kcContext={kcContext} i18n={i18n} headline={msg('emailForgotTitle')} subhead={msg('emailInstruction')}>
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{msg('emailForgotTitle')}</h2>
      <p className="text-body-md text-onSurfaceVariant mb-5">{msg('emailInstruction')}</p>

      {error && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{error}</p>
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
          containerClassName="mb-3.5"
        />
        <Button type="submit" title={msgStr('doSubmit')} loading={submitting} rounded="md" className="mt-1" />
      </form>

      <a href={url.loginUrl} className="block text-center mt-4.5 text-label-lg text-primary">
        {msg('backToLogin')}
      </a>
    </Template>
  );
}
