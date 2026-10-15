import { File as NodeFile } from 'node:buffer';

// jsdom's File lacks Blob.arrayBuffer; the worker runs where it exists, so tests use Node's implementation.
export const File = NodeFile as unknown as typeof globalThis.File;
