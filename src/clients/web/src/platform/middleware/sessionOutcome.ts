import type { Middleware } from '@microsoft/kiota-http-fetchlibrary';
import type { RequestOption } from '@microsoft/kiota-abstractions';
import { parseProblem } from '../failure';

export type SessionOutcome = 'sessionEnded' | 'passwordChangeRequired';

export class SessionOutcomeHandler implements Middleware {
  next: Middleware | undefined;

  constructor(private readonly onOutcome: (outcome: SessionOutcome) => void) {}

  async execute(url: string, init: RequestInit, options?: Record<string, RequestOption>): Promise<Response> {
    const response = await this.next!.execute(url, init, options);
    if (response.status === 401 || response.status === 403) {
      const problem = await readProblem(response);
      if (response.status === 403) {
        if (problem.code === 'password-change-required') this.onOutcome('passwordChangeRequired');
      } else if (problem.code !== 'sign-in-failed') {
        // A failed sign-in is not an ended session.
        this.onOutcome('sessionEnded');
      }
    }
    return response;
  }
}

async function readProblem(response: Response) {
  try {
    return parseProblem(await response.clone().json());
  } catch {
    return {};
  }
}
