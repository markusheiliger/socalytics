import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useConnectivity } from '../connectivity/useConnectivity';
import { createRecordingLineageAdapter } from '../platform/adapters/recordingLineageAdapter';
import { createRecordingUploadAdapter } from '../platform/adapters/recordingUploadAdapter';
import { useSession } from '../session/sessionContext';
import { UploadContext } from './uploadContext';
import { createUploadCoordinator, type UploadCoordinator, type WorkerLike } from './uploadCoordinator';
import { createUploadRepository, type UploadRepository } from './uploadRepository';
import { createUploadWorker } from './createUploadWorker';

export interface UploadProviderProps {
  children: ReactNode;
  /** Replaces the bundled worker, for tests. */
  createWorker?: () => WorkerLike;
}

// Owns the upload coordinator of the signed-in member. Jobs survive a session end and resume after the next sign-in
// of the same member; an explicit sign-out forgets the member's local upload records.
export function UploadProvider({ children, createWorker = () => createUploadWorker() as unknown as WorkerLike }: UploadProviderProps) {
  const { status, session, reason, platform } = useSession();
  const { isUnreachable } = useConnectivity();
  // The member stays known while the platform ended the session, so jobs pause instead of being dropped.
  const [memberId, setMemberId] = useState<string>();
  const sessionMemberId = session?.member.id;
  useEffect(() => {
    if (sessionMemberId) setMemberId(sessionMemberId);
  }, [sessionMemberId]);
  const [active, setActive] = useState<{ memberId: string; coordinator: UploadCoordinator; repository: UploadRepository }>();
  const createWorkerRef = useRef(createWorker);
  createWorkerRef.current = createWorker;
  const unreachableRef = useRef(isUnreachable);
  unreachableRef.current = isUnreachable;
  const platformRef = useRef(platform);
  platformRef.current = platform;

  useEffect(() => {
    if (!memberId) return;
    const coordinator = createUploadCoordinator({
      memberId,
      uploads: createRecordingUploadAdapter(platformRef.current),
      lineage: createRecordingLineageAdapter(platformRef.current),
      createWorker: () => createWorkerRef.current(),
    });
    coordinator.setUnreachable(unreachableRef.current);
    setActive({ memberId, coordinator, repository: createUploadRepository(coordinator) });
    return () => {
      coordinator.dispose();
      setActive((current) => (current?.coordinator === coordinator ? undefined : current));
    };
  }, [memberId]);

  const signedIn = status === 'signedIn' || status === 'passwordChangeRequired';
  useEffect(() => {
    active?.coordinator.setSessionActive(signedIn);
  }, [active, signedIn]);

  useEffect(() => {
    active?.coordinator.setUnreachable(isUnreachable);
  }, [active, isUnreachable]);

  // `session` is cleared with the member; keep the coordinator while the platform ended the session.
  const explicitSignOut = status === 'signedOut' && reason === undefined;
  useEffect(() => {
    if (explicitSignOut && active) void active.coordinator.purgeMember();
  }, [explicitSignOut, active]);

  const value = useMemo(() => (signedIn ? active?.repository : undefined), [signedIn, active]);
  return <UploadContext.Provider value={value}>{children}</UploadContext.Provider>;
}
