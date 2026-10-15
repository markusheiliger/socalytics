import { pathToFileURL } from 'node:url';

import { TaskInputError, runTypedCommand } from './speckit-implement-work.mjs';

const COMMANDS = ['package', 'land-tasks', 'repair-report'];

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
