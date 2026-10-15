import { createContext, useContext } from 'react';
import type { UploadRepository } from './uploadRepository';

/** Undefined while no member is signed in. */
export const UploadContext = createContext<UploadRepository | undefined>(undefined);

export function useUploads(): UploadRepository | undefined {
  return useContext(UploadContext);
}
