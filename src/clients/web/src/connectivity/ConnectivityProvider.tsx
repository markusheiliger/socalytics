import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ConnectivityContext, type ConnectivityState } from './connectivityContext';

export interface ConnectivityProviderProps {
  children: ReactNode;
  probeUrl?: string;
  initialDelayMs?: number;
  maxDelayMs?: number;
  probeTimeoutMs?: number;
}

export function ConnectivityProvider({
  children,
  probeUrl = '/alive',
  initialDelayMs = 1000,
  maxDelayMs = 30000,
  probeTimeoutMs = 5000,
}: ConnectivityProviderProps) {
  const queryClient = useQueryClient();
  const [online, setOnline] = useState(() => navigator.onLine);
  const [failed, setFailed] = useState(false);
  const [probeNonce, setProbeNonce] = useState(0);
  const unreachable = !online || failed;

  const reportNetworkFailure = useCallback(() => setFailed(true), []);

  useEffect(() => {
    const handleOnline = () => {
      setOnline(true);
      setProbeNonce((n) => n + 1);
      void queryClient.invalidateQueries();
    };
    const handleOffline = () => setOnline(false);
    window.addEventListener('online', handleOnline);
    window.addEventListener('offline', handleOffline);
    return () => {
      window.removeEventListener('online', handleOnline);
      window.removeEventListener('offline', handleOffline);
    };
  }, [queryClient]);

  useEffect(() => {
    if (!unreachable) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | undefined;

    const schedule = (delay: number) => {
      timer = setTimeout(() => void probe(delay), delay);
    };

    const probe = async (delay: number) => {
      controller = new AbortController();
      const abortTimer = setTimeout(() => controller?.abort(), probeTimeoutMs);
      let reachable = false;
      try {
        const response = await fetch(probeUrl, { cache: 'no-store', credentials: 'omit', signal: controller.signal });
        reachable = response.ok;
      } catch {
        reachable = false;
      } finally {
        clearTimeout(abortTimer);
      }
      if (cancelled) return;
      if (reachable) {
        setOnline(true);
        setFailed(false);
        void queryClient.invalidateQueries();
        return;
      }
      schedule(Math.min(delay * 2, maxDelayMs));
    };

    schedule(initialDelayMs);
    return () => {
      cancelled = true;
      clearTimeout(timer);
      controller?.abort();
    };
  }, [unreachable, probeNonce, probeUrl, initialDelayMs, maxDelayMs, probeTimeoutMs, queryClient]);

  const value = useMemo<ConnectivityState>(
    () => ({ status: unreachable ? 'unreachable' : 'online', isUnreachable: unreachable, reportNetworkFailure }),
    [unreachable, reportNetworkFailure],
  );

  return <ConnectivityContext.Provider value={value}>{children}</ConnectivityContext.Provider>;
}
