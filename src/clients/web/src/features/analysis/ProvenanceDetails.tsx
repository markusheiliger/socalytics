import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Provenance } from '../../platform/models/analysis';

const HEX_DIGEST = /^([a-z0-9][a-z0-9+._-]*:)([0-9a-f]+)$/i;

/** The algorithm prefix plus 12 hexadecimal characters; anything else is shown whole. */
export function shortenDigest(digest: string): string {
  const match = HEX_DIGEST.exec(digest);
  return match && match[2].length > 12 ? `${match[1]}${match[2].slice(0, 12)}` : digest;
}

type CopyOutcome = 'copied' | 'failed';

interface DigestDisclosureProps {
  digest: string;
  labelId: string;
  onCopy: (outcome: CopyOutcome) => void;
}

function DigestDisclosure({ digest, labelId, onCopy }: DigestDisclosureProps) {
  const { t } = useTranslation('analysis');
  const [open, setOpen] = useState(false);
  const id = useId();
  const toggleId = `${id}-toggle`;
  const copyId = `${id}-copy`;

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(digest);
      onCopy('copied');
    } catch {
      onCopy('failed');
    }
  };

  return (
    <>
      <Button
        id={toggleId}
        size="small"
        aria-expanded={open}
        aria-controls={`${id}-full`}
        aria-labelledby={`${toggleId} ${labelId}`}
        onClick={() => setOpen((value) => !value)}
      >
        {open ? t('provenance.hideDigest') : t('provenance.showDigest')}
      </Button>
      {open && (
        <div id={`${id}-full`} role="group" aria-label={t('provenance.fullDigest')}>
          <code style={{ userSelect: 'all', overflowWrap: 'anywhere' }}>{digest}</code>
          <Button id={copyId} size="small" aria-labelledby={`${copyId} ${labelId}`} onClick={() => void copy()}>
            {t('provenance.copy')}
          </Button>
        </div>
      )}
    </>
  );
}

interface ProvenanceDetailsProps {
  provenance: Provenance;
}

/** Workflow, Analyst profile, and image digests (plus the model digest when present) of a stream (FR-012, FR-016). */
export function ProvenanceDetails({ provenance }: ProvenanceDetailsProps) {
  const { t } = useTranslation('analysis');
  const [outcome, setOutcome] = useState<CopyOutcome | undefined>();
  const id = useId();
  const profileLabel = `${id}-profile`;
  const imageLabel = `${id}-image`;
  const modelLabel = `${id}-model`;

  return (
    <section aria-label={t('provenance.title')}>
      <Typography variant="h4" component="h4" sx={{ mt: 2 }}>
        {t('provenance.title')}
      </Typography>
      <dl>
        <div>
          <dt>{t('provenance.workflow')}</dt>
          <dd>{t('provenance.workflowValue', { id: provenance.workflow.id, version: provenance.workflow.version })}</dd>
        </div>
        <div>
          <dt id={profileLabel}>{t('provenance.analystProfile')}</dt>
          <dd>
            {t('provenance.analystProfileValue', {
              id: provenance.analystProfile.id,
              digest: shortenDigest(provenance.analystProfile.digest),
              interpolation: { escapeValue: false },
            })}
            <DigestDisclosure digest={provenance.analystProfile.digest} labelId={profileLabel} onCopy={setOutcome} />
          </dd>
        </div>
        <div>
          <dt id={imageLabel}>{t('provenance.imageDigest')}</dt>
          <dd>
            {shortenDigest(provenance.imageDigest)}
            <DigestDisclosure digest={provenance.imageDigest} labelId={imageLabel} onCopy={setOutcome} />
          </dd>
        </div>
        {provenance.modelDigest && (
          <div>
            <dt id={modelLabel}>{t('provenance.modelDigest')}</dt>
            <dd>
              {shortenDigest(provenance.modelDigest)}
              <DigestDisclosure digest={provenance.modelDigest} labelId={modelLabel} onCopy={setOutcome} />
            </dd>
          </div>
        )}
      </dl>
      <div role="status" aria-live="polite" aria-atomic="true">
        {outcome === 'copied' && t('provenance.copied')}
        {outcome === 'failed' && t('provenance.copyFailed')}
      </div>
    </section>
  );
}
