import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { ProfileCreatePage } from '../../../../src/features/registry/ProfileCreatePage';
import { RegistryRefusalError } from '../../../../src/platform/models/registry';
import { draftProfile, fakeRegistryAdapter, profiles, SEGMENT_DIGEST, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeRegistryAdapter, entry = `/registry/profiles/new?image=${encodeURIComponent(SEGMENT_DIGEST)}`) {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/registry/profiles/new', element: <ProfileCreatePage /> }], { initialEntries: [entry] });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

describe('ProfileCreatePage', () => {
  it('shows an image picker when the image parameter is missing', async () => {
    const adapter = fakeRegistryAdapter();
    const router = renderPage(adapter, '/registry/profiles/new');
    const picker = await screen.findByLabelText('Image');
    await userEvent.selectOptions(picker, SEGMENT_DIGEST);
    expect(router.state.location.search).toBe(`?image=${encodeURIComponent(SEGMENT_DIGEST)}`);
    expect(await screen.findByLabelText('Profile id')).toBeInTheDocument();
  });

  it('prefills the form from the declaration and creates a draft', async () => {
    const adapter = fakeRegistryAdapter({
      createProfile: vi.fn(async () => ({ profile: draftProfile, created: true })),
    });
    renderPage(adapter);
    expect(await screen.findByLabelText('Profile id')).toHaveValue('ball-tracking-legacy');
    expect(screen.getByLabelText('Runtime constraints')).toHaveValue('linux/amd64');
    expect(screen.getByLabelText('Resources')).toHaveValue('cpu=2\nmemory=4Gi');
    expect(screen.getByLabelText('Maximum attempts')).toHaveValue('2');
    await userEvent.click(screen.getByRole('button', { name: 'Create profile version' }));
    expect(await screen.findByText('Profile version created')).toBeInTheDocument();
    expect(adapter.createProfile).toHaveBeenCalledTimes(1);
    const draft = adapter.createProfile.mock.calls[0][0];
    expect(draft.imageDigest).toBe(SEGMENT_DIGEST);
    expect(draft.profileId).toBe('ball-tracking-legacy');
    expect(draft.content.resources).toEqual({ cpu: '2', memory: '4Gi' });
    expect(draft.content.executionTimeoutSeconds).toBe(600);
    expect(screen.getByRole('link', { name: 'Show the profile version' })).toHaveAttribute(
      'href',
      '/registry/profiles/ball-tracking-standard/versions/1',
    );
  });

  it('shows "already existed" for identical content', async () => {
    const adapter = fakeRegistryAdapter({ createProfile: vi.fn(async () => ({ profile: draftProfile, created: false })) });
    renderPage(adapter);
    await screen.findByLabelText('Profile id');
    await userEvent.click(screen.getByRole('button', { name: 'Create profile version' }));
    expect(await screen.findByText('This already existed, no new version was created.')).toBeInTheDocument();
  });

  it('explains invalid input before sending', async () => {
    const adapter = fakeRegistryAdapter({ listProfiles: vi.fn(async () => profiles.filter((p) => p.profileId === 'none')) });
    renderPage(adapter);
    const id = await screen.findByLabelText('Profile id');
    expect(id).toHaveValue('');
    const lease = screen.getByLabelText('Lease (seconds)');
    await userEvent.clear(lease);
    await userEvent.type(lease, 'abc');
    await userEvent.click(screen.getByRole('button', { name: 'Create profile version' }));
    expect(await screen.findByText('Enter a profile id.')).toBeInTheDocument();
    expect(screen.getByText('Enter a whole number greater than zero.')).toBeInTheDocument();
    expect(adapter.createProfile).not.toHaveBeenCalled();
  });

  it('shows contradictions next to their fields and as a refusal', async () => {
    const adapter = fakeRegistryAdapter({
      createProfile: vi.fn(async () => {
        throw new RegistryRefusalError({
          operation: 'createAnalystProfile',
          code: 'profile-contradicts-declaration',
          details: [{ field: 'content.executionTimeoutSeconds', code: 'below-minimum' }],
          retryable: false,
        });
      }),
    });
    renderPage(adapter);
    await screen.findByLabelText('Profile id');
    await userEvent.click(screen.getByRole('button', { name: 'Create profile version' }));
    expect(await screen.findByText("The profile content contradicts the image's capability declaration.")).toBeInTheDocument();
    expect(screen.getAllByText(/content\.executionTimeoutSeconds: below-minimum/).length).toBeGreaterThanOrEqual(2);
    expect(screen.getByLabelText('Profile id')).toHaveValue('ball-tracking-legacy');
  });
});
