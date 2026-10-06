import { useState, type ReactNode } from 'react';
import { KeyRound, Smartphone } from 'lucide-react';
import TextField from '@anamnys/shared/ui/TextField';
import { Template } from '../Template';
import RequiredActionFooter from '../components/RequiredActionFooter';
import type { KcContext } from '../KcContext';
import type { I18n } from '../i18n';

type Props = {
  kcContext: Extract<KcContext, { pageId: 'login-config-totp.ftl' }>;
  i18n: I18n;
};

// Keycloak switches between the QR and the manual-entry view with a full page load
// (totp.manualUrl / totp.qrUrl), so `mode` comes from the context, not local state.
export default function LoginConfigTotp({ kcContext, i18n }: Props) {
  const { url, isAppInitiatedAction, totp, mode, messagesPerField } = kcContext;
  const { msg, msgStr, advancedMsg } = i18n;
  const [submitting, setSubmitting] = useState(false);

  const error = messagesPerField.existsError('totp', 'userLabel')
    ? messagesPerField.getFirstError('totp', 'userLabel')
    : null;
  const deviceNameRequired = totp.otpCredentials.length >= 1;

  return (
    <Template kcContext={kcContext} i18n={i18n} headline={msg('loginTotpTitle')} subhead={msg('totpSubhead')}>
      <h2 className="text-headline-md text-[24px] text-onSurface mb-4">{msg('loginTotpTitle')}</h2>

      {error && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{error}</p>
        </div>
      )}

      <ol className="flex flex-col gap-4 mb-6">
        <Step n={1}>
          <p>{msg('loginTotpStep1')}</p>
          <ul className="mt-1.5 flex flex-wrap gap-2">
            {totp.supportedApplications.map((app) => (
              <li
                key={app}
                className="bg-surfaceContainerLow rounded-radii-md px-2.5 py-1 text-label-md normal-case text-onSurface"
              >
                {advancedMsg(app)}
              </li>
            ))}
          </ul>
        </Step>

        {mode === 'manual' ? (
          <>
            <Step n={2}>
              <p>{msg('loginTotpManualStep2')}</p>
              <p className="mt-2 font-mono text-[15px] tracking-wider break-all bg-surfaceContainerLow rounded-radii-md px-3 py-2 text-onSurface">
                {totp.totpSecretEncoded}
              </p>
              <a href={totp.qrUrl} className="inline-block mt-2 text-label-lg text-primary">
                {msg('loginTotpScanBarcode')}
              </a>
            </Step>
            <Step n={3}>
              <p>{msg('loginTotpManualStep3')}</p>
              <dl className="mt-1.5 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-body-md">
                <dt className="text-onSurfaceVariant">{msg('loginTotpType')}</dt>
                <dd>{msg(`loginTotp.${totp.policy.type}`)}</dd>
                <dt className="text-onSurfaceVariant">{msg('loginTotpAlgorithm')}</dt>
                <dd>{totp.policy.getAlgorithmKey()}</dd>
                <dt className="text-onSurfaceVariant">{msg('loginTotpDigits')}</dt>
                <dd>{totp.policy.digits}</dd>
                {totp.policy.type === 'totp' ? (
                  <>
                    <dt className="text-onSurfaceVariant">{msg('loginTotpInterval')}</dt>
                    <dd>{totp.policy.period}</dd>
                  </>
                ) : (
                  <>
                    <dt className="text-onSurfaceVariant">{msg('loginTotpCounter')}</dt>
                    <dd>{totp.policy.initialCounter}</dd>
                  </>
                )}
              </dl>
            </Step>
          </>
        ) : (
          <Step n={2}>
            <p>{msg('loginTotpStep2')}</p>
            <div className="mt-2 inline-block bg-white border border-outlineVariant rounded-radii-md p-2">
              <img
                src={`data:image/png;base64, ${totp.totpSecretQrCode}`}
                alt={msgStr('loginTotpStep2')}
                width={160}
                height={160}
                className="w-40 h-40"
              />
            </div>
            <a href={totp.manualUrl} className="block mt-2 text-label-lg text-primary">
              {msg('loginTotpUnableToScan')}
            </a>
          </Step>
        )}

        <Step n={mode === 'manual' ? 4 : 3}>
          <p>{msg('loginTotpStep3')}</p>
          <p className="mt-1 text-onSurfaceVariant">{msg('loginTotpStep3DeviceName')}</p>
        </Step>
      </ol>

      <form action={url.loginAction} method="post" onSubmit={() => setSubmitting(true)}>
        <TextField
          name="totp"
          label={`${msgStr('authenticatorCode')} *`}
          icon={KeyRound}
          autoComplete="one-time-code"
          inputMode="numeric"
          containerClassName="mb-3.5"
        />
        <TextField
          name="userLabel"
          label={deviceNameRequired ? `${msgStr('loginTotpDeviceName')} *` : msgStr('loginTotpDeviceName')}
          icon={Smartphone}
          autoComplete="off"
          containerClassName="mb-4"
        />
        <input type="hidden" name="totpSecret" value={totp.totpSecret} />
        {mode && <input type="hidden" name="mode" value={mode} />}
        <RequiredActionFooter i18n={i18n} submitting={submitting} isAppInitiatedAction={isAppInitiatedAction} />
      </form>
    </Template>
  );
}

function Step({ n, children }: { n: number; children: ReactNode }) {
  return (
    <li className="flex gap-3 text-body-md text-onSurface">
      <span className="w-6 h-6 shrink-0 rounded-radii-full bg-primaryFixed text-onPrimaryFixedVariant text-label-md flex items-center justify-center">
        {n}
      </span>
      <div className="flex-1 pt-0.5">{children}</div>
    </li>
  );
}
