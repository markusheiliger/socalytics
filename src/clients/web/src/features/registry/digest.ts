const SHORT_LENGTH = 12;
const DIGEST_FORM = /^([a-z0-9]+(?:[+._-][a-z0-9]+)*):([0-9a-f]+)$/i;

/** The algorithm plus the first 12 hexadecimal characters; unknown forms and values that are already short stay unchanged (FR-010). */
export const shortDigest = (value: string) => {
  const match = DIGEST_FORM.exec(value);
  if (!match || match[2].length <= SHORT_LENGTH) {
    return value;
  }
  return `${match[1]}:${match[2].slice(0, SHORT_LENGTH)}`;
};
