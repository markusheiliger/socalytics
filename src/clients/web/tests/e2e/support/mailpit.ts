// The AppHost in e2e mode publishes Mailpit's web UI and REST API on a fixed port.
const MAILPIT_URL = process.env.SOCALYTICS_E2E_MAILPIT_URL ?? 'http://127.0.0.1:4176';

type MailpitMessageSummary = { ID: string; Subject: string };
type MailpitSearch = { messages: MailpitMessageSummary[] };
type MailpitMessage = { Text: string };

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(`${MAILPIT_URL}${path}`);
  if (!response.ok) throw new Error(`Mailpit ${path} answered ${response.status}`);
  return (await response.json()) as T;
}

/** Waits until Mailpit holds a message addressed to the recipient and returns its plain-text body. */
export async function waitForMessageText(recipient: string, timeoutMs = 90_000, subject?: string): Promise<string> {
  const deadline = Date.now() + timeoutMs;
  const query = subject ? `to:${recipient} subject:"${subject}"` : `to:${recipient}`;
  for (;;) {
    const found = await getJson<MailpitSearch>(`/api/v1/search?query=${encodeURIComponent(query)}`);
    const latest = found.messages[0];
    if (latest) return (await getJson<MailpitMessage>(`/api/v1/message/${encodeURIComponent(latest.ID)}`)).Text;
    if (Date.now() > deadline) throw new Error(`No message for ${recipient} arrived in Mailpit within ${timeoutMs} ms.`);
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
}

/** Reads the first confirmation link (with its `#token=` fragment) from a message body. */
export function extractConfirmationLink(text: string): URL {
  const match = /https?:\/\/[^\s<>"]+\/email\/confirm#token=[^\s<>"]+/.exec(text);
  if (!match) throw new Error('The message contains no confirmation link.');
  return new URL(match[0]);
}
