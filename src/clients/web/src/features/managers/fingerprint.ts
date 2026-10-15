/** Letters and digits only, upper-case (research.md § R5). */
export const normalizeFingerprint = (value: string) => value.replace(/[^0-9a-z]/gi, '').toUpperCase();

export const CONFIRMATION_LENGTH = 6;

/** The last 6 normalized characters of a fingerprint, the human check before an approval. */
export const lastCharacters = (fingerprint: string) => normalizeFingerprint(fingerprint).slice(-CONFIRMATION_LENGTH);

export const matchesFingerprint = (input: string, fingerprint: string) => {
  const entered = normalizeFingerprint(input);
  return entered !== '' && entered === lastCharacters(fingerprint);
};

/** Revocation is confirmed by the host label (trimmed, exact) or, without one, the last 6 fingerprint characters (FR-012). */
export const matchesRevocation = (input: string, hostLabel: string | undefined, fingerprint: string) => {
  const label = hostLabel?.trim();
  return label ? input.trim() === label : matchesFingerprint(input, fingerprint);
};
