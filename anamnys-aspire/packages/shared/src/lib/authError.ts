// The BFF's OIDC handler redirects to the app's landing path with
// ?authError=true when the Keycloak login itself succeeded but the server
// refused to establish a local identity — an uninvited patient, an
// owners-realm token carrying none of the staff roles, a disabled account.
//
// Retrying is pointless and harmful: a fresh challenge succeeds at Keycloak
// again (the SSO session is live) and fails provisioning again, so an auth
// gate that reacts to "no user" by calling login() spins forever with no
// visible error. Every gate must check this first and stop.
export const hasAuthError = (): boolean =>
  new URLSearchParams(window.location.search).get("authError") === "true";
