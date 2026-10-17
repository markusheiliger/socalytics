import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type { ProfileVersion } from '../../platform/models/registry';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { DigestText } from './DigestText';
import { useProfiles } from './registryKeys';

const KNOWN_STATES = ['draft', 'approved', 'retired'];

function groupByProfile(items: ProfileVersion[]): [string, ProfileVersion[]][] {
  const groups = new Map<string, ProfileVersion[]>();
  for (const item of items) groups.set(item.profileId, [...(groups.get(item.profileId) ?? []), item]);
  return [...groups.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([id, versions]) => [id, [...versions].sort((a, b) => a.version - b.version)]);
}

/** The profile versions grouped by profile id, filtered by state and capability id kept in the address (FR-006). */
export function ProfilesPage() {
  const { t } = useTranslation('registry');
  const { t: tc } = useTranslation('common');
  const [params, setParams] = useSearchParams();
  const state = params.get('state')?.trim() || undefined;
  const capabilityId = params.get('capabilityId')?.trim() || undefined;
  const [stateText, setStateText] = useState(state ?? '');
  const [capabilityText, setCapabilityText] = useState(capabilityId ?? '');
  useEffect(() => setStateText(state ?? ''), [state]);
  useEffect(() => setCapabilityText(capabilityId ?? ''), [capabilityId]);
  const query = useProfiles({ state, capabilityId });
  const failure = query.error ? toPlatformFailure(query.error) : undefined;
  const filtered = state !== undefined || capabilityId !== undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const stateLabel = (value: string) => t(`profileState.${value}`, { defaultValue: value });
  const stateOptions = state && !KNOWN_STATES.includes(state) ? [...KNOWN_STATES, state] : KNOWN_STATES;

  const apply = (event: FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    if (stateText.trim()) next.state = stateText.trim();
    if (capabilityText.trim()) next.capabilityId = capabilityText.trim();
    setParams(next);
  };

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('profiles.title')}</Typography>
      <Link to="/registry">{t('overview.title')}</Link>
      <Stack component="form" direction="row" spacing={1} onSubmit={apply} sx={{ mt: 2, alignItems: 'center', flexWrap: 'wrap' }}>
        <TextField
          select
          size="small"
          label={t('profiles.filterState')}
          value={stateText}
          onChange={(e) => setStateText(e.target.value)}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
          sx={{ minWidth: 160 }}
        >
          <option value="">{t('profiles.filterAllStates')}</option>
          {stateOptions.map((value) => (
            <option key={value} value={value}>{stateLabel(value)}</option>
          ))}
        </TextField>
        <TextField size="small" label={t('profiles.filterCapability')} value={capabilityText} onChange={(e) => setCapabilityText(e.target.value)} />
        <Button type="submit" variant="outlined">{t('profiles.applyFilter')}</Button>
        {filtered && <Button type="button" onClick={() => setParams({})}>{t('profiles.clearFilter')}</Button>}
      </Stack>
      {failure && <FailureAlert failure={failure} onReload={() => void query.refetch()} />}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {query.data &&
        (query.data.length === 0 ? (
          <Typography sx={{ mt: 2 }}>{filtered ? t('profiles.emptyFiltered') : t('profiles.empty')}</Typography>
        ) : (
          <Stack component="ul" aria-label={t('profiles.listLabel')} spacing={2} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
            {groupByProfile(query.data).map(([profileId, versions]) => (
              <li key={profileId}>
                <Typography variant="h3">{profileId}</Typography>
                <Stack component="ul" aria-label={t('profiles.groupLabel', { profileId })} spacing={1} sx={{ listStyle: 'none', m: 0, p: 0 }}>
                  {versions.map((p) => (
                    <li key={p.version}>
                      <Link to={`/registry/profiles/${encodeURIComponent(p.profileId)}/versions/${p.version}`}>
                        {t('profiles.listEntry', { version: p.version, state: stateLabel(p.state) })}
                      </Link>
                      <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
                        <li>{t('profiles.capability', { capability: p.content.capabilityId, version: p.content.capabilityVersion })}</li>
                        <li><DigestText value={p.imageDigest} /></li>
                      </Stack>
                    </li>
                  ))}
                </Stack>
              </li>
            ))}
          </Stack>
        ))}
    </Container>
  );
}
