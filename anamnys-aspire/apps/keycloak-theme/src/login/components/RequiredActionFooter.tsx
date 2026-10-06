import { useState } from 'react';
import Button from '@anamnys/shared/ui/Button';
import Checkbox from '@anamnys/shared/ui/Checkbox';
import type { I18n } from '../i18n';

type Props = {
  i18n: I18n;
  submitting: boolean;
  // True when the action was started from the app ("Acesso e segurança"), so the user may
  // back out. Keycloak reads the cancel as a submit carrying cancel-aia=true.
  isAppInitiatedAction?: boolean;
};

// Shared tail of the required-action forms (update password, configure TOTP): the
// "sign out other devices" option and the submit/cancel buttons. Checkbox is a styled
// <button>, so the hidden input is what actually posts logout-sessions=on.
export default function RequiredActionFooter({ i18n, submitting, isAppInitiatedAction }: Props) {
  const { msg, msgStr } = i18n;
  const [logoutSessions, setLogoutSessions] = useState(false);

  return (
    <>
      <Checkbox
        checked={logoutSessions}
        onToggle={() => setLogoutSessions((v) => !v)}
        className="mb-5 text-body-md text-onSurfaceVariant"
      >
        {msg('logoutOtherSessions')}
      </Checkbox>
      {logoutSessions && <input type="hidden" name="logout-sessions" value="on" />}

      <div className="flex flex-col-reverse sm:flex-row gap-3">
        {isAppInitiatedAction && (
          <Button
            type="submit"
            name="cancel-aia"
            value="true"
            variant="outline"
            title={msgStr('doCancel')}
            rounded="md"
            fullWidth={false}
            className="sm:flex-1"
          />
        )}
        <Button
          type="submit"
          title={msgStr('doSubmit')}
          loading={submitting}
          rounded="md"
          fullWidth={false}
          className="sm:flex-1"
        />
      </div>
    </>
  );
}
