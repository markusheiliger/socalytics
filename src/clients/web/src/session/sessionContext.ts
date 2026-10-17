import { createContext, useContext } from 'react';
import type { IdentityProvider } from '../platform/adapters/sessionAdapter';
import type { Session } from '../platform/models/session';
import type { PlatformFailure } from '../platform/failure';
import type { createPlatformApi } from '../platform/client';

export type SessionStatus = 'unknown' | 'signedOut' | 'passwordChangeRequired' | 'signedIn';

export interface SessionState {
  status: SessionStatus;
  session?: Session;
  /** Why the member is signed out: the platform ended the session. */
  reason?: 'sessionEnded';
  /** Set while `unknown` when restoring failed, for example because the platform is unreachable. */
  restoreFailure?: PlatformFailure;
}

export interface SessionApi extends SessionState {
  platform: ReturnType<typeof createPlatformApi>;
  signIn(accountName: string, password: string): Promise<void>;
  listProviders(): Promise<IdentityProvider[]>;
  startExternalSignIn(provider: string, link?: { accountName: string; linkCredential: string }): Promise<string>;
  signOut(): Promise<void>;
  changeOwnPassword(currentPassword: string, newPassword: string): Promise<void>;
  redeemCredential(accountName: string, code: string, newPassword: string): Promise<void>;
  clearReason(): void;
}

export const SessionContext = createContext<SessionApi | undefined>(undefined);

export function useSession(): SessionApi {
  const value = useContext(SessionContext);
  if (!value) throw new Error('useSession must be used inside SessionProvider');
  return value;
}
