import "@tanstack/react-router";

// History state (never in the URL) passed between provider routes.
declare module "@tanstack/react-router" {
  interface HistoryState {
    // The create modal saved the patient but could not send the requested invitation.
    inviteError?: string;
  }
}
