import { useState } from 'react';
import { KeyRound } from 'lucide-react';
import Button from '@anamnys/shared/ui/Button';
import TextField from '@anamnys/shared/ui/TextField';
import { Template } from '../Template';
import type { KcContext } from '../KcContext';
import type { I18n } from '../i18n';

type Props = {
  kcContext: Extract<KcContext, { pageId: 'login-otp.ftl' }>;
  i18n: I18n;
};

export default function LoginOtp({ kcContext, i18n }: Props) {
  const { url, messagesPerField } = kcContext;
  const { msg, msgStr } = i18n;
  const [submitting, setSubmitting] = useState(false);

  const error = messagesPerField.existsError('totp') ? messagesPerField.get('totp') : null;

  return (
    <Template kcContext={kcContext} i18n={i18n} headline={msg('doLogIn')} subhead={msg('loginOtpOneTime')}>
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{msg('loginOtpOneTime')}</h2>

      {error && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{error}</p>
        </div>
      )}

      <form action={url.loginAction} method="post" onSubmit={() => setSubmitting(true)}>
        <TextField
          name="otp"
          label={msgStr('loginOtpOneTime')}
          icon={KeyRound}
          autoCapitalize="none"
          autoComplete="one-time-code"
          inputMode="numeric"
          containerClassName="mb-3.5"
        />
        <Button type="submit" title={msgStr('doLogIn')} loading={submitting} rounded="md" className="mt-1" />
      </form>
    </Template>
  );
}
