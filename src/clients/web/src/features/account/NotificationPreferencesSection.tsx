import Alert from '@mui/material/Alert';
import FormControlLabel from '@mui/material/FormControlLabel';
import Stack from '@mui/material/Stack';
import Switch from '@mui/material/Switch';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createEmailAdapter } from '../../platform/adapters/emailAdapter';
import { toPlatformFailure, type PlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';

const KEY = ['email', 'preferences'];

/** The notification types that apply to the member's roles: toggles, and account messages shown as always sent. */
export function NotificationPreferencesSection() {
  const { t } = useTranslation('email');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createEmailAdapter(platform), [platform]);
  const [failure, setFailure] = useState<PlatformFailure>();
  const query = useQuery({ queryKey: KEY, queryFn: () => adapter.preferences(), retry: false });

  const change = useMutation({
    mutationFn: ({ type, enabled }: { type: string; enabled: boolean }) => adapter.setPreference(type, enabled),
    onMutate: () => setFailure(undefined),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: KEY }),
    onError: (error) => setFailure(toPlatformFailure(error)),
  });

  const label = (type: string) => t(`preferences.types.${type}`, { defaultValue: type });

  return (
    <Stack component="section" aria-labelledby="preferences-title" spacing={2} sx={{ mt: 3 }}>
      <Typography id="preferences-title" variant="h3">
        {t('preferences.title')}
      </Typography>
      {query.isError && (
        <Alert severity="error" role="status">
          {t('preferences.loadFailed')}
        </Alert>
      )}
      {failure && <FailureAlert failure={failure} onReload={() => void query.refetch()} />}
      {query.isSuccess && query.data.length === 0 && <Typography>{t('preferences.none')}</Typography>}
      {query.isSuccess && query.data.length > 0 && (
        <Stack component="ul" spacing={1} sx={{ listStyle: 'none', m: 0, p: 0 }}>
          {query.data.map((item) => (
            <li key={item.type}>
              {item.accountMessage ? (
                <Alert severity="info" icon={false} role="note">
                  {t('preferences.alwaysSent', { type: label(item.type) })}
                </Alert>
              ) : (
                <FormControlLabel
                  label={label(item.type)}
                  control={
                    <Switch
                      checked={item.enabled}
                      disabled={change.isPending || isUnreachable}
                      onChange={(e) => change.mutate({ type: item.type, enabled: e.target.checked })}
                    />
                  }
                />
              )}
            </li>
          ))}
        </Stack>
      )}
    </Stack>
  );
}
