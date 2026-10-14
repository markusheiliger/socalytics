/** Letters and digits only, upper-case (research.md § R5). */
export const normalizeFingerprint = (value: string) => value.replace(/[^0-9a-z]/gi, '').toUpperCase();

export const CONFIRMATION_LENGTH = 6;

/** The last 6 normalized characters of a fingerprint, the human check before an approval. */
export const lastCharacters = (fingerprint: string) => normalizeFingerprint(fingerprint).slice(-CONFIRMATION_LENGTH);

export const matchesFingerprint = (input: string, fingerprint: string) => {
  const entered = normalizeFingerprint(input);
  return entered !== '' && entered === lastCharacters(fingerprint);
};
