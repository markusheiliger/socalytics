import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import type { PlatformFailure } from '../../platform/failure';
import type { HomeAway, Match, MatchInput } from '../../platform/models/matches';
import { FailureAlert, useFieldError } from '../shared/FailureAlert';

export const VENUES: readonly HomeAway[] = ['home', 'away', 'neutral'];

const pad = (n: number) => String(n).padStart(2, '0');

/** The value of a `datetime-local` input in the member's local time. */
export function toLocalInput(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

export function formatKickoff(date: Date): string {
  return date.toLocaleString('en', { dateStyle: 'medium', timeStyle: 'short' });
}

interface MatchFormProps {
  /** Present when editing; the opponent is then read-only. */
  match?: Match;
  submitting: boolean;
  failure?: PlatformFailure;
  submitLabel: string;
  submittingLabel: string;
  title: string;
  onSubmit: (input: MatchInput) => void;
  onReload?: () => void;
}

/** Details of a match: kickoff time, venue, competition, and the opponent only at creation. */
export function MatchForm({ match, submitting, failure, submitLabel, submittingLabel, title, onSubmit, onReload }: MatchFormProps) {
  const { t } = useTranslation('matches');
  const { t: tc } = useTranslation('common');
  const { isUnreachable } = useConnectivity();
  const [opponent, setOpponent] = useState(match?.opponentName ?? '');
  const [kickoff, setKickoff] = useState(match ? toLocalInput(match.kickoffAt) : '');
  const [homeAway, setHomeAway] = useState<HomeAway>(match?.homeAway ?? 'home');
  const [competition, setCompetition] = useState(match?.competition ?? '');
  const [kickoffInvalid, setKickoffInvalid] = useState(false);
  const opponentError = useFieldError(failure, 'opponent.name', tc);
  const kickoffError = useFieldError(failure, 'kickoffAt', tc);
  const competitionError = useFieldError(failure, 'competition', tc);
  const fieldShown = failure?.kind === 'validation' && Boolean(opponentError ?? kickoffError ?? competitionError);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const date = new Date(kickoff);
    if (!kickoff || Number.isNaN(date.getTime())) {
      setKickoffInvalid(true);
      return;
    }
    setKickoffInvalid(false);
    onSubmit({ opponentName: opponent.trim(), kickoffAt: date, homeAway, competition: competition.trim() || undefined });
  };

  return (
    <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={submit} noValidate aria-label={title}>
      {failure && !fieldShown && <FailureAlert failure={failure} onReload={onReload} />}
      <TextField
        label={t('matches.opponent')}
        name="opponent"
        value={opponent}
        onChange={(e) => setOpponent(e.target.value)}
        error={Boolean(opponentError)}
        helperText={match ? t('matches.opponentFixed') : opponentError}
        slotProps={{ input: { readOnly: Boolean(match) } }}
        required
      />
      <TextField
        label={t('matches.kickoff')}
        name="kickoffAt"
        type="datetime-local"
        value={kickoff}
        onChange={(e) => setKickoff(e.target.value)}
        error={kickoffInvalid || Boolean(kickoffError)}
        helperText={kickoffInvalid ? t('matches.invalidKickoff') : kickoffError}
        slotProps={{ inputLabel: { shrink: true } }}
        required
      />
      <TextField
        select
        label={t('matches.venue')}
        name="homeAway"
        value={homeAway}
        onChange={(e) => setHomeAway(e.target.value as HomeAway)}
        slotProps={{ select: { native: true } }}
      >
        {VENUES.map((v) => (
          <option key={v} value={v}>
            {t(`matches.venueOption.${v}`)}
          </option>
        ))}
      </TextField>
      <TextField
        label={t('matches.competition')}
        name="competition"
        value={competition}
        onChange={(e) => setCompetition(e.target.value)}
        error={Boolean(competitionError)}
        helperText={competitionError}
      />
      <Button type="submit" variant="contained" disabled={submitting || isUnreachable}>
        {submitting ? submittingLabel : submitLabel}
      </Button>
    </Stack>
  );
}
