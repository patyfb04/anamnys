import { useState } from 'react';
import { Lock } from 'lucide-react';
import TextField from '@anamnys/shared/ui/TextField';
import { Template } from '../Template';
import RequiredActionFooter from '../components/RequiredActionFooter';
import type { KcContext } from '../KcContext';
import type { I18n } from '../i18n';

type Props = {
  kcContext: Extract<KcContext, { pageId: 'login-update-password.ftl' }>;
  i18n: I18n;
};

export default function LoginUpdatePassword({ kcContext, i18n }: Props) {
  const { url, messagesPerField, isAppInitiatedAction } = kcContext;
  const { msg, msgStr } = i18n;
  const [submitting, setSubmitting] = useState(false);

  const error = messagesPerField.existsError('password', 'password-confirm')
    ? messagesPerField.getFirstError('password', 'password-confirm')
    : null;

  return (
    <Template
      kcContext={kcContext}
      i18n={i18n}
      headline={msg('updatePasswordTitle')}
      subhead={msg('updatePasswordSubhead')}
    >
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{msg('updatePasswordTitle')}</h2>
      <p className="text-body-md text-onSurfaceVariant mb-5">{msg('updatePasswordSubhead')}</p>

      {error && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{error}</p>
        </div>
      )}

      <form action={url.loginAction} method="post" onSubmit={() => setSubmitting(true)}>
        <TextField
          name="password-new"
          label={msgStr('passwordNew')}
          icon={Lock}
          secureToggle
          autoFocus
          autoComplete="new-password"
          containerClassName="mb-3.5"
        />
        <TextField
          name="password-confirm"
          label={msgStr('passwordConfirm')}
          icon={Lock}
          secureToggle
          autoComplete="new-password"
          containerClassName="mb-4"
        />
        <RequiredActionFooter i18n={i18n} submitting={submitting} isAppInitiatedAction={isAppInitiatedAction} />
      </form>
    </Template>
  );
}
