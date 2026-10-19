import { act, renderHook } from '@testing-library/react';
import { useCreationIntent, useQuestionDraft } from '../../../../src/features/triage/useQuestionDraft';

describe('useQuestionDraft', () => {
  it('creates a key on the first send and reuses it for an unchanged resend after an unknown outcome', () => {
    const { result } = renderHook(() => useQuestionDraft());
    act(() => {
      result.current.setAgentId('agent-a');
      result.current.setText('Why the late goal?');
    });
    let first = '';
    act(() => {
      first = result.current.startSend();
    });
    expect(first).toMatch(/^[0-9a-f-]{36}$/);
    act(() => result.current.settle('unknown'));
    expect(result.current.outcome).toBe('unknown');
    expect(result.current.idempotencyKey).toBe(first);
    let second = '';
    act(() => {
      second = result.current.startSend();
    });
    expect(second).toBe(first);
  });

  it('drops the key when the text changes', () => {
    const { result } = renderHook(() => useQuestionDraft());
    act(() => {
      result.current.setAgentId('agent-a');
      result.current.setText('One');
    });
    let first = '';
    act(() => {
      first = result.current.startSend();
    });
    act(() => result.current.settle('unknown'));
    act(() => result.current.setText('Two'));
    expect(result.current.idempotencyKey).toBeUndefined();
    expect(result.current.outcome).toBeUndefined();
    let second = '';
    act(() => {
      second = result.current.startSend();
    });
    expect(second).not.toBe(first);
  });

  it('drops the key when the addressed agent changes', () => {
    const { result } = renderHook(() => useQuestionDraft());
    act(() => {
      result.current.setAgentId('agent-a');
      result.current.setText('One');
    });
    let first = '';
    act(() => {
      first = result.current.startSend();
    });
    act(() => result.current.settle('unknown'));
    act(() => result.current.setAgentId('agent-b'));
    expect(result.current.idempotencyKey).toBeUndefined();
    let second = '';
    act(() => {
      second = result.current.startSend();
    });
    expect(second).not.toBe(first);
  });

  it.each(['accepted', 'refused'] as const)('drops the key on a definite answer (%s)', (answer) => {
    const { result } = renderHook(() => useQuestionDraft());
    act(() => {
      result.current.setAgentId('agent-a');
      result.current.setText('One');
    });
    let first = '';
    act(() => {
      first = result.current.startSend();
    });
    act(() => result.current.settle(answer));
    expect(result.current.idempotencyKey).toBeUndefined();
    expect(result.current.outcome).toBe(answer);
    let second = '';
    act(() => {
      second = result.current.startSend();
    });
    expect(second).not.toBe(first);
  });

  it('clears the text and key on reset but keeps the agent', () => {
    const { result } = renderHook(() => useQuestionDraft());
    act(() => {
      result.current.setAgentId('agent-a');
      result.current.setText('One');
    });
    act(() => {
      result.current.startSend();
    });
    act(() => result.current.reset());
    expect(result.current.text).toBe('');
    expect(result.current.agentId).toBe('agent-a');
    expect(result.current.idempotencyKey).toBeUndefined();
  });

  it('keeps nothing in browser storage', () => {
    const { result } = renderHook(() => useQuestionDraft());
    act(() => {
      result.current.setText('One');
    });
    act(() => {
      result.current.startSend();
    });
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });
});

describe('useCreationIntent', () => {
  it('reuses the key after an unknown outcome and drops it on a definite answer', () => {
    const { result } = renderHook(() => useCreationIntent('run-1'));
    let first = '';
    act(() => {
      first = result.current.startSend();
    });
    act(() => result.current.settle('unknown'));
    let again = '';
    act(() => {
      again = result.current.startSend();
    });
    expect(again).toBe(first);
    act(() => result.current.settle('accepted'));
    expect(result.current.idempotencyKey).toBeUndefined();
    let next = '';
    act(() => {
      next = result.current.startSend();
    });
    expect(next).not.toBe(first);
  });

  it('uses a new key for a different analysis run', () => {
    const { result, rerender } = renderHook(({ run }) => useCreationIntent(run), { initialProps: { run: 'run-1' } });
    let first = '';
    act(() => {
      first = result.current.startSend();
    });
    act(() => result.current.settle('unknown'));
    rerender({ run: 'run-2' });
    let second = '';
    act(() => {
      second = result.current.startSend();
    });
    expect(second).not.toBe(first);
  });
});
