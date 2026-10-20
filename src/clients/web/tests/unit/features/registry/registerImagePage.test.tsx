import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { ImagePage } from '../../../../src/features/registry/ImagePage';
import { RegisterImagePage } from '../../../../src/features/registry/RegisterImagePage';
import { RegistryRefusalError, type RegistryRefusal } from '../../../../src/platform/models/registry';
import { fakeRegistryAdapter, segmentDeclaration, SEGMENT_DIGEST, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

const REFERENCE = `registry.example.org/analysts/segment@sha256:${'a'.repeat(64)}`;

function renderPage(adapter: FakeRegistryAdapter) {
  state.adapter = adapter;
  const router = createMemoryRouter(
    [
      { path: '/registry/images/register', element: <RegisterImagePage /> },
      { path: '/registry/images/:digest', element: <ImagePage /> },
    ],
    { initialEntries: ['/registry/images/register'] },
  );
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

function refusal(code: string, extra: Partial<RegistryRefusal> = {}): RegistryRefusalError {
  return new RegistryRefusalError({ operation: 'registerAnalystImage', code, details: [], retryable: false, ...extra });
}

async function submit(value: string) {
  const input = screen.getByLabelText('Image reference');
  await userEvent.clear(input);
  await userEvent.type(input, value);
  await userEvent.click(screen.getByRole('button', { name: 'Register image' }));
}

describe('RegisterImagePage', () => {
  it.each([
    ['a tag reference', 'registry.example.org/analysts/segment:latest'],
    ['a short digest', 'registry.example.org/analysts/segment@sha256:abc'],
    ['a non-hexadecimal digest', `registry.example.org/analysts/segment@sha256:${'z'.repeat(64)}`],
  ])('explains %s before sending', async (_name, value) => {
    const adapter = fakeRegistryAdapter();
    renderPage(adapter);
    await submit(value);
    expect(await screen.findByText(/must contain @sha256:/)).toBeInTheDocument();
    expect(adapter.registerImage).not.toHaveBeenCalled();
  });

  it('shows progress and disables a second submission', async () => {
    type Registered = { declaration: typeof segmentDeclaration; created: boolean };
    let resolve: (value: Registered) => void = () => {};
    const adapter = fakeRegistryAdapter({ registerImage: vi.fn(() => new Promise<Registered>((r) => { resolve = r; })) });
    renderPage(adapter);
    await submit(REFERENCE);
    const button = await screen.findByRole('button', { name: 'Registering…' });
    expect(button).toBeDisabled();
    expect(screen.getByRole('status')).toHaveTextContent('Registering…');
    expect(adapter.registerImage).toHaveBeenCalledTimes(1);
    resolve({ declaration: segmentDeclaration, created: true });
    expect(await screen.findByRole('heading', { name: 'Analyst image' })).toBeInTheDocument();
  });

  it('opens the image details after a new registration', async () => {
    const adapter = fakeRegistryAdapter();
    const router = renderPage(adapter);
    await submit(REFERENCE);
    expect(await screen.findByRole('heading', { name: 'Analyst image' })).toBeInTheDocument();
    expect(adapter.registerImage).toHaveBeenCalledWith(REFERENCE);
    expect(router.state.location.pathname).toBe(`/registry/images/${encodeURIComponent(SEGMENT_DIGEST)}`);
    expect(screen.queryByText('The image was already registered, nothing changed.')).not.toBeInTheDocument();
  });

  it('says nothing changed when the image was already registered', async () => {
    renderPage(fakeRegistryAdapter({ registerImage: vi.fn(async () => ({ declaration: segmentDeclaration, created: false })) }));
    await submit(REFERENCE);
    expect(await screen.findByText('The image was already registered, nothing changed.')).toBeInTheDocument();
  });

  it.each([
    ['reference-not-digest', 'The reference is not pinned to a digest.'],
    ['reference-not-allowed', 'not allowed'],
    ['image-not-found', 'The image was not found in the registry.'],
    ['registry-unavailable', 'Try again later.', true],
    ['digest-mismatch', 'does not match the digest'],
    ['manifest-missing', 'carries no Analyst manifest'],
    ['manifest-invalid', "manifest is invalid"],
    ['labels-mismatch', 'labels do not match'],
    ['unsupported-media-type', 'media type that is not supported'],
  ] as [string, string, boolean?][])('shows the refusal %s and keeps the reference', async (code, text, retryable) => {
    const details = [{ field: 'reference', code }];
    renderPage(fakeRegistryAdapter({ registerImage: vi.fn(async () => { throw refusal(code, { details, retryable: retryable ?? false }); }) }));
    await submit(REFERENCE);
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(text);
    expect(alert).toHaveTextContent(`reference: ${code}`);
    expect(screen.getByLabelText('Image reference')).toHaveValue(REFERENCE);
    expect(screen.getByRole('button', { name: 'Register image' })).toBeEnabled();
  });

  it('adds "try again later" for a retryable refusal and omits it otherwise', async () => {
    renderPage(fakeRegistryAdapter({ registerImage: vi.fn(async () => { throw refusal('some-new-code', { retryable: true }); }) }));
    await submit(REFERENCE);
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('some-new-code');
    expect(alert).toHaveTextContent('Try again later.');
  });

  it('does not suggest trying again for a permanent refusal', async () => {
    renderPage(fakeRegistryAdapter({ registerImage: vi.fn(async () => { throw refusal('image-not-found'); }) }));
    await submit(REFERENCE);
    expect(await screen.findByRole('alert')).not.toHaveTextContent('Try again later.');
  });
});
