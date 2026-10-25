import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Development limits (research R4); the primary attempt limits live in action.yml.
export const ALTERNATIVE_FEED_URL = 'https://ci.dot.net/public';
export const ALTERNATIVE_ATTEMPTS = 1;
export const ALTERNATIVE_TIMEOUT_SECONDS = 240;
export const ALTERNATIVE_DOWNLOAD_TIMEOUT_SECONDS = 30;
export const COMMAND_TIMEOUT_SECONDS = 30;
export const COMMAND_DEADLINE_SECONDS = 120;

const notImplemented = (name) => {
  throw new Error(`${name} is not implemented yet`);
};

export function parseRequirement(...args) {
  return notImplemented('parseRequirement', args);
}

export function satisfies(...args) {
  return notImplemented('satisfies', args);
}

export function acceptable(...args) {
  return notImplemented('acceptable', args);
}

export function decide(...args) {
  return notImplemented('decide', args);
}

export function formatFailure(...args) {
  return notImplemented('formatFailure', args);
}

export function formatSummary(...args) {
  return notImplemented('formatSummary', args);
}

export async function main(argv = process.argv.slice(2)) {
  const [command] = argv;
  if (command !== 'resolve') {
    console.error('Usage: dotnet-resolve.mjs resolve');
    return 2;
  }
  console.error('The resolve command is not implemented yet');
  return 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = await main();
}
