import type { Middleware } from '@microsoft/kiota-http-fetchlibrary';
import { describe, expect, it, vi } from 'vitest';
import { MinimumVersionHandler, minimumVersionHeader } from '../../../src/platform/middleware/minimumVersion';

function setup(header: string | undefined, running: string | null = '2026.1015.100000') {
  let blocked = false;
  const send = vi.fn(async () => new Response(null, { status: 200, headers: header ? { [minimumVersionHeader]: header } : {} }));
  const onRequired = vi.fn(() => {
    blocked = true;
  });
  const handler = new MinimumVersionHandler({ running: () => running ?? undefined, isBlocked: () => blocked, onRequired });
  handler.next = { next: undefined, execute: send } as Middleware;
  return { handler, send, onRequired };
}

describe('MinimumVersionHandler', () => {
  it('blocks every later request without sending it when the minimum is higher', async () => {
    const { handler, send, onRequired } = setup('2026.1015.110000');
    await handler.execute('/a', {});
    expect(onRequired).toHaveBeenCalledWith('2026.1015.110000');
    await expect(handler.execute('/b', {})).rejects.toMatchObject({ kind: 'updateRequired' });
    expect(send).toHaveBeenCalledTimes(1);
  });

  it.each(['2026.1015.100000', '2026.1014.235959', '2025.1231.235959'])('does not block for %s', async (minimum) => {
    const { handler, send, onRequired } = setup(minimum);
    await handler.execute('/a', {});
    await handler.execute('/b', {});
    expect(onRequired).not.toHaveBeenCalled();
    expect(send).toHaveBeenCalledTimes(2);
  });

  it('compares as integers, not as text', async () => {
    const { onRequired, handler } = setup('2026.1015.100001');
    await handler.execute('/a', {});
    expect(onRequired).toHaveBeenCalled();
  });

  it('ignores an absent, malformed, or unrunnable comparison', async () => {
    for (const [header, running] of [[undefined, '2026.1015.100000'], ['garbage', '2026.1015.100000'], ['2026.1015.110000', null]] as const) {
      const { handler, onRequired } = setup(header, running);
      await handler.execute('/a', {});
      expect(onRequired).not.toHaveBeenCalled();
    }
  });
});
