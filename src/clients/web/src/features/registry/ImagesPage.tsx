import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { DigestText } from './DigestText';
import { useImages } from './registryKeys';

/** The registered Analyst images, optionally filtered by capability id kept in the address (FR-004). */
export function ImagesPage() {
  const { t } = useTranslation('registry');
  const { t: tc } = useTranslation('common');
  const [params, setParams] = useSearchParams();
  const capabilityId = params.get('capabilityId')?.trim() || undefined;
  const [text, setText] = useState(capabilityId ?? '');
  useEffect(() => setText(capabilityId ?? ''), [capabilityId]);
  const query = useImages(capabilityId);
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const apply = (event: FormEvent) => {
    event.preventDefault();
    const value = text.trim();
    setParams(value ? { capabilityId: value } : {});
  };

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('images.title')}</Typography>
      <Stack direction="row" spacing={2}>
        <Link to="/registry">{t('overview.title')}</Link>
        <Link to="/registry/images/register">{t('images.register')}</Link>
      </Stack>
      <Stack component="form" direction="row" spacing={1} onSubmit={apply} sx={{ mt: 2, alignItems: 'center' }}>
        <TextField size="small" label={t('images.filterCapability')} value={text} onChange={(e) => setText(e.target.value)} />
        <Button type="submit" variant="outlined">{t('images.applyFilter')}</Button>
        {capabilityId && (
          <Button type="button" onClick={() => setParams({})}>{t('images.clearFilter')}</Button>
        )}
      </Stack>
      {failure && <FailureAlert failure={failure} onReload={() => void query.refetch()} />}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {query.data &&
        (query.data.length === 0 ? (
          <Typography sx={{ mt: 2 }}>{capabilityId ? t('images.emptyFiltered') : t('images.empty')}</Typography>
        ) : (
          <Stack component="ul" aria-label={t('images.listLabel')} spacing={2} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
            {query.data.map((image) => (
              <li key={image.imageDigest}>
                <Link to={`/registry/images/${encodeURIComponent(image.imageDigest)}`}>
                  {t('images.listEntry', { capability: image.capabilityId, version: image.capabilityVersion })}
                </Link>
                <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
                  <li><DigestText value={image.imageDigest} /></li>
                  <li>{t('images.registeredAt', { value: image.registeredAt.toLocaleString() })}</li>
                </Stack>
              </li>
            ))}
          </Stack>
        ))}
    </Container>
  );
}
