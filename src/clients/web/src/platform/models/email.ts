/** A notification type that applies to the member; account messages are always sent. */
export interface NotificationPreference {
  type: string;
  enabled: boolean;
  accountMessage: boolean;
}

export type EmailState = 'unverified' | 'verified';

/** The address of a member and whether its owner confirmed it. */
export interface EmailAddress {
  address: string;
  state: EmailState;
  verifiedAt?: Date;
  version: number;
}
