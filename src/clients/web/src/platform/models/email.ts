export type EmailState = 'unverified' | 'verified';

/** The address of a member and whether its owner confirmed it. */
export interface EmailAddress {
  address: string;
  state: EmailState;
  verifiedAt?: Date;
  version: number;
}
