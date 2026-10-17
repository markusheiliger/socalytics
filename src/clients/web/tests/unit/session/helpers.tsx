import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { appRoutes } from '../../../src/router';
import { ConnectivityProvider } from '../../../src/connectivity/ConnectivityProvider';
import type { SessionAdapter } from '../../../src/platform/adapters/sessionAdapter';
import type { ClubRole, Session, TeamRole } from '../../../src/platform/models/session';
import { SessionProvider } from '../../../src/session/SessionProvider';
import '../../../src/i18n';

export function makeSession(
  options: { clubRoles?: ClubRole[]; teamRoles?: TeamRole[]; passwordChangeRequired?: boolean; token?: string } = {},
): Session {
  return {
    sessionId: 's-1',
    idleExpiresAt: new Date('2030-01-01T00:00:00Z'),
    absoluteExpiresAt: new Date('2030-01-02T00:00:00Z'),
    antiforgeryToken: options.token ?? 'csrf-1',
    passwordChangeRequired: options.passwordChangeRequired ?? false,
    member: {
      id: 'm-1',
      accountName: 'ada',
      membershipStatus: 'Active',
      clubRoles: options.clubRoles ?? [],
      teamRoles: options.teamRoles ?? [],
    },
  };
}

export function fakeAdapter(overrides: Partial<SessionAdapter> = {}): SessionAdapter {
  return {
    listProviders: vi.fn().mockResolvedValue([]),
    startExternalSignIn: vi.fn(),
    signIn: vi.fn(),
    restore: vi.fn(),
    signOut: vi.fn().mockResolvedValue(undefined),
    changeOwnPassword: vi.fn().mockResolvedValue(undefined),
    redeemCredential: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
}

export function renderApp(adapter: SessionAdapter, path = '/', queryClient = new QueryClient()) {
  const router = createMemoryRouter(appRoutes, { initialEntries: [path] });
  render(
    <QueryClientProvider client={queryClient}>
      <ConnectivityProvider>
        <SessionProvider adapter={adapter}>
          <RouterProvider router={router} />
        </SessionProvider>
      </ConnectivityProvider>
    </QueryClientProvider>,
  );
  return { router, queryClient };
}
