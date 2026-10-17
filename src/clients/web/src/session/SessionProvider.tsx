import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useConnectivity } from '../connectivity/useConnectivity';
import { createSessionAdapter, type SessionAdapter } from '../platform/adapters/sessionAdapter';
import { createPlatformApi } from '../platform/client';
import { toPlatformFailure } from '../platform/failure';
import type { Session } from '../platform/models/session';
import type { SessionOutcome } from '../platform/middleware/sessionOutcome';
import { SessionContext, type SessionApi, type SessionState } from './sessionContext';

export interface SessionProviderProps {
  children: ReactNode;
  /** Replaces the platform-backed adapter, for tests. */
  adapter?: SessionAdapter;
}

const SIGNED_OUT: SessionState = { status: 'signedOut' };

export function SessionProvider({ children, adapter: injected }: SessionProviderProps) {
  const queryClient = useQueryClient();
  const { reportNetworkFailure, isUnreachable } = useConnectivity();
  const [state, setState] = useState<SessionState>({ status: 'unknown' });
  // The anti-forgery token lives in memory only, never in storage.
  const tokenRef = useRef<string | undefined>(undefined);
  const statusRef = useRef(state.status);
  statusRef.current = state.status;
  const networkRef = useRef(reportNetworkFailure);
  networkRef.current = reportNetworkFailure;

  const apply = useCallback((next: SessionState) => {
    tokenRef.current = next.session?.antiforgeryToken;
    setState(next);
  }, []);

  const clearSession = useCallback(
    (next: SessionState) => {
      apply(next);
      queryClient.clear();
    },
    [apply, queryClient],
  );

  const endedRef = useRef<(outcome: SessionOutcome) => void>(() => {});
  endedRef.current = (outcome) => {
    if (outcome === 'sessionEnded') {
      // A rejected restore or sign-in while signed out is not an ended session.
      const wasSignedIn = statusRef.current === 'signedIn' || statusRef.current === 'passwordChangeRequired';
      clearSession(wasSignedIn ? { status: 'signedOut', reason: 'sessionEnded' } : SIGNED_OUT);
    } else if (statusRef.current === 'signedIn') {
      setState((current) =>
        current.session ? { status: 'passwordChangeRequired', session: { ...current.session, passwordChangeRequired: true } } : current,
      );
    }
  };

  const platform = useMemo(
    () =>
      createPlatformApi({
        getAntiforgeryToken: () => tokenRef.current,
        onSessionOutcome: (outcome) => endedRef.current(outcome),
        onNetworkFailure: () => networkRef.current(),
      }),
    [],
  );
  const adapter = useMemo(() => injected ?? createSessionAdapter(platform), [injected, platform]);

  const fromSession = (session: Session): SessionState => ({
    status: session.passwordChangeRequired ? 'passwordChangeRequired' : 'signedIn',
    session,
  });

  const restore = useCallback(async () => {
    try {
      apply(fromSession(await adapter.restore()));
    } catch (error) {
      const failure = toPlatformFailure(error);
      if (failure.kind === 'sessionEnded') {
        apply(SIGNED_OUT);
      } else {
        setState({ status: 'unknown', restoreFailure: failure });
      }
    }
  }, [adapter, apply]);

  useEffect(() => {
    void restore();
  }, [restore]);

  const failedRestore = state.status === 'unknown' && state.restoreFailure !== undefined;
  const wasUnreachable = useRef(false);
  useEffect(() => {
    if (wasUnreachable.current && !isUnreachable && failedRestore) void restore();
    wasUnreachable.current = isUnreachable;
  }, [isUnreachable, failedRestore, restore]);

  const api = useMemo<SessionApi>(
    () => ({
      ...state,
      platform,
      signIn: async (accountName, password) => {
        queryClient.clear();
        apply(fromSession(await adapter.signIn(accountName, password)));
      },
      listProviders: () => adapter.listProviders(),
      startExternalSignIn: (provider) => adapter.startExternalSignIn(provider),
      signOut: async () => {
        try {
          await adapter.signOut();
        } catch (error) {
          const failure = toPlatformFailure(error);
          if (failure.kind !== 'sessionEnded') throw failure;
        }
        clearSession(SIGNED_OUT);
      },
      changeOwnPassword: async (currentPassword, newPassword) => {
        await adapter.changeOwnPassword(currentPassword, newPassword);
        // Re-read the session: the platform clears the password-change requirement.
        apply(fromSession(await adapter.restore()));
      },
      redeemCredential: (accountName, code, newPassword) => adapter.redeemCredential(accountName, code, newPassword),
      clearReason: () => setState((current) => (current.reason ? { ...current, reason: undefined } : current)),
    }),
    [state, platform, adapter, apply, clearSession, queryClient],
  );

  return <SessionContext.Provider value={api}>{children}</SessionContext.Provider>;
}
