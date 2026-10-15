import type { TimelineSpan } from '../platform/models/recordings';
import type { AddFileInput, AddFilesResult, UploadCoordinator, UploadJob } from './uploadCoordinator';

// The screens' read and command interface; nothing else talks to the coordinator.
export interface UploadRepository {
  subscribe(listener: () => void): () => void;
  getSnapshot(): readonly UploadJob[];
  jobsForMatch(matchId: string): readonly UploadJob[];
  /** False once the browser refused to keep the local upload record. */
  journalAvailable(): boolean;
  loadMatch(matchId: string): Promise<void>;
  addFiles(matchId: string, teamId: string, inputs: readonly AddFileInput[]): AddFilesResult;
  setMapping(jobId: string, mapping: readonly TimelineSpan[]): void;
  retry(jobId: string): void;
  pause(jobId: string): void;
  resume(jobId: string): void;
  removeFromQueue(jobId: string): boolean;
  reselectFile(jobId: string, file: File): Promise<void>;
  abandon(jobId: string): void;
  acknowledgeExpiry(jobId: string): void;
  purgeMember(): Promise<void>;
}

export function createUploadRepository(coordinator: UploadCoordinator): UploadRepository {
  return {
    subscribe: (listener) => coordinator.subscribe(listener),
    getSnapshot: () => coordinator.getSnapshot(),
    jobsForMatch: (matchId) => coordinator.getSnapshot().filter((job) => job.matchId === matchId),
    journalAvailable: () => coordinator.journalAvailable(),
    loadMatch: (matchId) => coordinator.loadJournal(matchId),
    addFiles: (matchId, teamId, inputs) => coordinator.addFiles(matchId, teamId, inputs),
    setMapping: (jobId, mapping) => coordinator.setMapping(jobId, mapping),
    retry: (jobId) => coordinator.retry(jobId),
    pause: (jobId) => coordinator.pause(jobId),
    resume: (jobId) => coordinator.resume(jobId),
    removeFromQueue: (jobId) => coordinator.removeFromQueue(jobId),
    reselectFile: (jobId, file) => coordinator.reselectFile(jobId, file),
    abandon: (jobId) => coordinator.abandon(jobId),
    acknowledgeExpiry: (jobId) => coordinator.acknowledgeExpiry(jobId),
    purgeMember: () => coordinator.purgeMember(),
  };
}
