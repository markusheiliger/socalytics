import { useEffect } from 'react';
import type { UploadJobState } from './uploadJob';

/** States in which closing or reloading the page interrupts an upload (FR-012). */
export const leaveGuardedStates: readonly UploadJobState[] = ['transferring', 'starting', 'completing'];

export function shouldGuardLeave(jobs: readonly { readonly state: UploadJobState }[]): boolean {
  return jobs.some((job) => leaveGuardedStates.includes(job.state));
}

/** Asks the browser to confirm before the page is closed or reloaded while `active`. */
export function useLeaveGuard(active: boolean): void {
  useEffect(() => {
    if (!active) return;
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // Some browsers only show the confirmation when a return value is set.
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [active]);
}
