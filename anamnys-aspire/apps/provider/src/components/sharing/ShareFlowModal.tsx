import { useState } from "react";
import type { CreatedShareLink } from "@anamnys/shared/lib/types";
import AuthorizationStep from "./AuthorizationStep";
import LinkOptionsStep from "./LinkOptionsStep";
import LinkResultStep from "./LinkResultStep";

export interface RecordedAuthorization {
  id: string;
  recipientName: string;
  existing?: boolean;
}

interface Props {
  documentId: string;
  /** Set to skip step 1 and add a link to an authorization already on record. */
  authorization: RecordedAuthorization | null;
  onChanged: () => void;
  onClose: () => void;
}

// Authorization, link options, then the one-time link and password. Each change is on the
// server by the time its step ends, so the list refreshes as the flow moves on.
export default function ShareFlowModal({ documentId, authorization: initial, onChanged, onClose }: Props) {
  const [authorization, setAuthorization] = useState(initial);
  const [link, setLink] = useState<CreatedShareLink | null>(null);

  if (link) return <LinkResultStep link={link} onClose={onClose} />;
  if (authorization)
    return (
      <LinkOptionsStep
        documentId={documentId}
        authorizationId={authorization.id}
        recipientName={authorization.recipientName}
        reusedAuthorization={authorization.existing ?? false}
        onClose={onClose}
        onBlocked={onChanged}
        onCreated={(created) => {
          setLink(created);
          onChanged();
        }}
      />
    );
  return (
    <AuthorizationStep
      documentId={documentId}
      onClose={onClose}
      onBlocked={onChanged}
      onRecorded={(recorded) => {
        setAuthorization(recorded);
        onChanged();
      }}
    />
  );
}
