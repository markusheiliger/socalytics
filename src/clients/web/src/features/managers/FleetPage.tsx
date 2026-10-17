import Container from '@mui/material/Container';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { createRegistrationAdapter } from '../../platform/adapters/registrationAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { isOwnSubmission } from '../../platform/models/registrations';
import { FailureAlert } from '../shared/FailureAlert';
import { arrangeFleet, DEFAULT_FLEET_FILTER, fleetQueryState, isFleetFilter, STATE_FILTERS, type FleetFilter } from './fleetFilter';
import { StatusLines } from './StatusLines';

export const FLEET_KEY = ['analyst-managers'];
export const PENDING_KEY = [...FLEET_KEY, 'pending'];
export const FLEET_REFRESH_MS = 30_000;
export const PENDING_COUNT_REFRESH_MS = 60_000;

export const HOST_LABEL_MAX_DISPLAY = 40;
const shorten = (value: string) => (value.length > HOST_LABEL_MAX_DISPLAY ? `${value.slice(0, HOST_LABEL_MAX_DISPLAY - 1)}…` : value);

/** The club's Analyst Managers for a Club Admin: current registrations by default, pending first (FR-010, FR-011). */
export function FleetPage() {
  const { t } = useTranslation('managers');
  const { t: tc } = useTranslation('common');
  const { platform, session } = useSession();
  const adapter = useMemo(() => createRegistrationAdapter(platform), [platform]);
  const [filter, setFilter] = useState<FleetFilter>(DEFAULT_FLEET_FILTER);
  const state = fleetQueryState(filter);
  const query = useQuery({
    queryKey: [...FLEET_KEY, 'list', state ?? 'unfiltered'],
    queryFn: () => adapter.listAll(state ? { state } : {}),
    retry: false,
    refetchInterval: FLEET_REFRESH_MS,
  });
  const items = useMemo(() => (query.data ? arrangeFleet(query.data, filter) : undefined), [query.data, filter]);
  const none = t('pairing.notProvided');
  const filterName = (f: FleetFilter) => (STATE_FILTERS.includes(f as never) ? t(`state.${f}`) : t(`fleet.filters.${f}`));

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('fleet.title')}</Typography>
      <TextField
        select
        size="small"
        label={t('fleet.filter')}
        value={filter}
        onChange={(e) => isFleetFilter(e.target.value) && setFilter(e.target.value)}
        sx={{ mt: 2, minWidth: 200 }}
       
      >
        {(['current', 'ended', 'all', ...STATE_FILTERS] as FleetFilter[]).map((f) => (
          <MenuItem key={f} value={f}>{filterName(f)}</MenuItem>
        ))}
      </TextField>
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {items &&
        (items.length === 0 ? (
          <Typography sx={{ mt: 2 }}>{t('fleet.empty')}</Typography>
        ) : (
          <Stack component="ul" aria-label={t('fleet.listLabel')} spacing={2} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
            {items.map((r) => {
              const host = r.deviceMetadata.hostLabel ?? none;
              const link = (
                <Link to={`/analyst-managers/${r.registrationId}`}>
                  {t('requests.listEntry', { host: shorten(host), state: t(`state.${r.state}`) })}
                </Link>
              );
              return (
                <li key={r.registrationId}>
                  {host.length > HOST_LABEL_MAX_DISPLAY ? (
                    <Tooltip title={host} describeChild>{link}</Tooltip>
                  ) : (
                    link
                  )}
                  <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
                    {isOwnSubmission(r, session?.member.id) && <li>{t('fleet.ownSubmission')}</li>}
                    <li>{t('requests.fingerprint', { value: r.deviceFingerprint })}</li>
                    <li>{t('fleet.keyProtection', { value: t(`keyProtection.${r.deviceMetadata.claimedKeyProtection}`) })}</li>
                    <li>{t('fleet.profile', { value: t(`applicationProfile.${r.deviceMetadata.claimedApplicationProfile ?? 'notReported'}`) })}</li>
                    <StatusLines r={r} />
                    {r.state === 'pendingApproval' && r.submittedAt && <li>{t('requests.submittedAt', { value: r.submittedAt.toLocaleString() })}</li>}
                    {r.state === 'pendingApproval' && r.approvalExpiresAt && <li>{t('requests.approvalDeadline', { value: r.approvalExpiresAt.toLocaleString() })}</li>}
                  </Stack>
                </li>
              );
            })}
          </Stack>
        ))}
    </Container>
  );
}
