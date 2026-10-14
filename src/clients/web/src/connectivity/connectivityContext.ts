import { createContext } from 'react';

export interface ConnectivityState {
  /** `unreachable` while the browser is offline or the platform could not be reached. */
  status: 'online' | 'unreachable';
  isUnreachable: boolean;
  /** Reports a network failure observed by the platform access layer. */
  reportNetworkFailure: () => void;
}

export const ConnectivityContext = createContext<ConnectivityState>({
  status: 'online',
  isUnreachable: false,
  reportNetworkFailure: () => {},
});
