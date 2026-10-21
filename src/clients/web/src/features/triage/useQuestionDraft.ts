import { useCallback, useRef, useState } from 'react';

/** `unknown` is a network failure after sending; `accepted` and `refused` are definite answers. */
export type SendOutcome = 'unknown' | 'accepted' | 'refused';

interface Intent {
  key: string;
  signature: string;
}

/**
 * The shared idempotency rule: a key is created on the first send of an intent, reused for a resend of the unchanged
 * intent while the outcome is unknown, and dropped when the intent changes or a definite answer arrives. Keys live in
 * memory only (FR-019).
 */
function useIntentKey() {
  const intent = useRef<Intent | null>(null);
  const [key, setKey] = useState<string | undefined>(undefined);

  const beginSend = useCallback((signature: string): string => {
    if (intent.current && intent.current.signature === signature) return intent.current.key;
    const created = { key: crypto.randomUUID(), signature };
    intent.current = created;
    setKey(created.key);
    return created.key;
  }, []);

  const drop = useCallback(() => {
    intent.current = null;
    setKey(undefined);
  }, []);

  return { key, beginSend, drop };
}

const signatureOf = (...parts: string[]) => JSON.stringify(parts);

export function useQuestionDraft() {
  const [text, setTextState] = useState('');
  const [agentId, setAgentIdState] = useState('');
  const [outcome, setOutcome] = useState<SendOutcome | undefined>(undefined);
  const { key, beginSend, drop } = useIntentKey();

  const setText = useCallback(
    (value: string) => {
      if (value !== text) {
        drop();
        setOutcome(undefined);
      }
      setTextState(value);
    },
    [drop, text],
  );

  const setAgentId = useCallback(
    (value: string) => {
      if (value !== agentId) {
        drop();
        setOutcome(undefined);
      }
      setAgentIdState(value);
    },
    [drop, agentId],
  );

  /** Returns the key for sending the current text to the addressed agent. */
  const startSend = useCallback(() => beginSend(signatureOf(agentId, text)), [beginSend, agentId, text]);

  const settle = useCallback(
    (result: SendOutcome) => {
      setOutcome(result);
      if (result !== 'unknown') drop();
    },
    [drop],
  );

  /** Clears the text after an accepted question; the addressed agent stays. */
  const reset = useCallback(() => {
    setTextState('');
    setOutcome(undefined);
    drop();
  }, [drop]);

  return { text, agentId, idempotencyKey: key, outcome, setText, setAgentId, startSend, settle, reset };
}

/** The same rule for conversation creation; a different analysis run is a different intent. */
export function useCreationIntent(analysisRunId: string) {
  const [outcome, setOutcome] = useState<SendOutcome | undefined>(undefined);
  const { key, beginSend, drop } = useIntentKey();

  const startSend = useCallback(() => beginSend(signatureOf(analysisRunId)), [beginSend, analysisRunId]);

  const settle = useCallback(
    (result: SendOutcome) => {
      setOutcome(result);
      if (result !== 'unknown') drop();
    },
    [drop],
  );

  const reset = useCallback(() => {
    setOutcome(undefined);
    drop();
  }, [drop]);

  return { analysisRunId, idempotencyKey: key, outcome, startSend, settle, reset };
}
