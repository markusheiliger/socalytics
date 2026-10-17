import { pathToFileURL } from 'node:url';

import { TaskInputError, runTypedCommand } from './speckit-implement-work.mjs';

const COMMANDS = ['package', 'land-tasks', 'repair-report'];

// A repair diff alone omits the code that originally failed, especially after a no-op.
export function repairVerificationScope({ scope, originalChangedFiles = null, repairChangedFiles = null }) {
  const paths = (text) => typeof text === 'string' ? text.split(/\r?\n/).filter((file) => file.trim().length > 0) : [];
  const original = paths(originalChangedFiles);
  const changedFiles = [...new Set([...original, ...paths(repairChangedFiles)])].sort();
  return {
    mode: scope === 'merge' || original.length === 0 || repairChangedFiles === null ? 'finalize' : 'phase',
    changedFiles,
    environment: changedFiles.some((file) => /^\.github\/actions\/environment-(setup|verify)\//.test(file)),
  };
}

export function main(argv, options = {}) {
  return runTypedCommand('repair', COMMANDS, argv, options);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof TaskInputError ? 2 : 1;
    },
  );
}
