// Mirrors the server's PasswordPolicy exactly (ClinicalDraft.Application/Common/PasswordPolicy.cs)
// so the UI never accepts a password the backend would reject: min 10 characters, plus at
// least one lowercase, one uppercase, one digit, and one symbol.
export const PASSWORD_MIN_LENGTH = 10;

export interface PasswordRequirement {
  key: 'length' | 'lowercase' | 'uppercase' | 'digit' | 'symbol';
  met: boolean;
}

// Returns each rule with whether the current password satisfies it — callers render this as a
// live checklist (see AccountSettingsScreen / RegisterScreen) rather than only rejecting on submit.
export function getPasswordRequirements(password: string): PasswordRequirement[] {
  return [
    { key: 'length', met: password.length >= PASSWORD_MIN_LENGTH },
    { key: 'lowercase', met: /[a-z]/.test(password) },
    { key: 'uppercase', met: /[A-Z]/.test(password) },
    { key: 'digit', met: /[0-9]/.test(password) },
    { key: 'symbol', met: /[^a-zA-Z0-9]/.test(password) },
  ];
}

export function isPasswordStrong(password: string): boolean {
  return getPasswordRequirements(password).every((r) => r.met);
}
