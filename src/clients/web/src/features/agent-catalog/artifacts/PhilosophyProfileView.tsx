import Box from '@mui/material/Box';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import type { Distance, DimensionSetDocument, PhilosophyProfileDocument } from '../../../platform/models/agentCatalog';
import { DistanceList } from './DistanceList';
import { Field, ValueList } from './ViewParts';

interface Props {
  document: PhilosophyProfileDocument;
  /** The identifier of this profile; it links the superseded version. */
  profileId: string;
  /** The referenced dimension set; it gives the order and meaning of the rows. Without it the rows follow the stored weights. */
  dimensionSet?: DimensionSetDocument;
  distances?: readonly Distance[];
}

const segment = (value: string) => encodeURIComponent(value);

export function PhilosophyProfileView({ document, profileId, dimensionSet, distances }: Props) {
  const { t } = useTranslation('agentCatalog');
  const ref = document.dimensionSet;
  const known = dimensionSet?.dimensions ?? [];
  const ids = [...known.map((d) => d.id), ...Object.keys(document.weights).filter((id) => !known.some((d) => d.id === id))];
  const total = Object.values(document.weights).reduce((sum, w) => sum + w.weight, 0);

  return (
    <Box component="dl" sx={{ m: 0 }}>
      <Field label={t('fields.dimensionSet')}>
        <Link to={`/agent-catalog/artifacts/dimension-sets/${segment(ref.id)}/versions/${segment(ref.version)}`}>
          {ref.id} {ref.version}
        </Link>
      </Field>
      <Field label={t('fields.weights')}>
        {ids.length === 0 ? (
          t('empty.weights')
        ) : (
          <Table size="small" aria-label={t('fields.weights')}>
            <TableHead>
              <TableRow>
                <TableCell>{t('fields.dimensionId')}</TableCell>
                <TableCell>{t('fields.meaning')}</TableCell>
                <TableCell>{t('fields.weight')}</TableCell>
                <TableCell>{t('fields.rationale')}</TableCell>
                <TableCell>{t('fields.provenance')}</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {ids.map((id) => {
                const weight = document.weights[id];
                return (
                  <TableRow key={id}>
                    <TableCell component="th" scope="row">
                      {id}
                    </TableCell>
                    <TableCell>{known.find((d) => d.id === id)?.meaning ?? t('common.none')}</TableCell>
                    <TableCell>{weight ? weight.weight : t('common.none')}</TableCell>
                    <TableCell>{weight ? weight.rationale : t('common.none')}</TableCell>
                    <TableCell>{weight ? weight.provenance : t('common.none')}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </Field>
      <Field label={t('fields.weightsTotal')}>{total}</Field>
      <Field label={t('fields.author')}>{document.author}</Field>
      <Field label={t('fields.architectureReviewers')}>
        <ValueList values={document.reviewers.architecture} text={(v) => v} empty={t('empty.reviewers')} />
      </Field>
      <Field label={t('fields.footballDomainReviewers')}>
        <ValueList values={document.reviewers.footballDomain} text={(v) => v} empty={t('empty.reviewers')} />
      </Field>
      <Field label={t('fields.supersedes')}>
        {document.supersedes ? (
          <Link to={`/agent-catalog/artifacts/philosophy-profiles/${segment(profileId)}/versions/${segment(document.supersedes.version)}`}>
            {document.supersedes.version}
          </Link>
        ) : (
          t('common.none')
        )}
      </Field>
      {distances !== undefined && (
        <Field label={t('distances.title')}>
          <DistanceList distances={distances} />
        </Field>
      )}
    </Box>
  );
}
