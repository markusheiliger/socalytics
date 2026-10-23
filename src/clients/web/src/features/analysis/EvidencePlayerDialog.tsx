import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { AnalysisEvidenceAdapter } from '../../platform/adapters/analysisEvidenceAdapter';
import { hasReachedEnd, matchTimeAt, seekTargetForMatchTime, type PlayablePart } from './evidenceTimeline';
import { formatMatchRange } from './matchClock';
import { useEvidencePlayback } from './useEvidencePlayback';

interface Props {
  adapter: AnalysisEvidenceAdapter;
  runId: string;
  fact: { acceptedResultId: string; ordinal: number; startMs: number; endMs: number };
  capability: string;
  onClose: () => void;
}

function firstSeekable(video: HTMLVideoElement): number {
  return video.seekable.length > 0 ? video.seekable.start(0) : 0;
}

function release(video: HTMLVideoElement | null) {
  if (!video) return;
  video.pause();
  video.removeAttribute('src');
  video.load();
}

/** Plays the evidence of one fact across segments with two alternating video elements; resolutions, links, and media live only while the dialog is open (FR-017–FR-021). */
export function EvidencePlayerDialog({ adapter, runId, fact, capability, onClose }: Props) {
  const { t } = useTranslation('analysis');
  const titleId = useId();
  const playback = useEvidencePlayback(adapter, runId, { acceptedResultId: fact.acceptedResultId, ordinal: fact.ordinal });
  const { status, playing, pending, nextPlayable, matchMs, setPosition, advance, mediaFailed } = playback;
  const videos = useRef<(HTMLVideoElement | null)[]>([null, null]);
  const assigned = useRef<(string | undefined)[]>([undefined, undefined]);
  const [active, setActive] = useState(0);
  const matchMsRef = useRef(matchMs);
  matchMsRef.current = matchMs;
  const [delayElapsed, setDelayElapsed] = useState(false);

  // The dialog content mounts inside a portal, so the elements are released when React detaches them.
  const videoRefs = useRef(
    [0, 1].map((index) => (element: HTMLVideoElement | null) => {
      if (element) videos.current[index] = element;
      else {
        release(videos.current[index]);
        videos.current[index] = null;
        assigned.current[index] = undefined;
      }
    }),
  );

  const start = useCallback((video: HTMLVideoElement, part: PlayablePart) => {
    const begin = () => {
      video.currentTime = seekTargetForMatchTime(firstSeekable(video), part, matchMsRef.current ?? part.startMs);
      void video.play().catch(() => {
        // The browser blocked unmuted playback: start muted.
        video.muted = true;
        void video.play().catch(() => undefined);
      });
    };
    if (video.readyState >= 1) begin();
    else video.addEventListener('loadedmetadata', begin, { once: true });
  }, []);

  const playingUrl = playing?.media.url;
  useEffect(() => {
    const [first, second] = videos.current;
    if (!first || !second) return;
    if (!playing) {
      first.pause();
      second.pause();
      return;
    }
    let index = assigned.current.indexOf(playing.media.url);
    if (index < 0) {
      index = active;
      videos.current[index]!.src = playing.media.url;
      assigned.current[index] = playing.media.url;
    }
    const other = 1 - index;
    videos.current[other]!.pause();
    setActive(index);
    start(videos.current[index]!, playing);
    // The part's identity is its link and window; match-time progress inside it must not restart playback.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [playingUrl, playing?.startMs, playing?.endMs]);

  const nextUrl = nextPlayable?.media.url;
  useEffect(() => {
    if (!nextUrl || !playingUrl || nextUrl === playingUrl) return;
    const standby = 1 - active;
    const video = videos.current[standby];
    if (video && assigned.current[standby] !== nextUrl && assigned.current[active] !== nextUrl) {
      video.src = nextUrl;
      assigned.current[standby] = nextUrl;
    }
  }, [nextUrl, playingUrl, active]);

  useEffect(() => {
    if (status !== 'rateLimited') return undefined;
    setDelayElapsed(false);
    const timer = setTimeout(() => setDelayElapsed(true), (playback.retryAfterSeconds ?? 5) * 1000);
    return () => clearTimeout(timer);
  }, [status, playback.retryAfterSeconds]);

  const onTimeUpdate = (index: number) => {
    const video = videos.current[index];
    if (!video || index !== active || !playing) return;
    const seekable = firstSeekable(video);
    if (hasReachedEnd(seekable, playing.media, video.currentTime)) advance();
    else setPosition(matchTimeAt(seekable, playing, video.currentTime));
  };

  const factRange = formatMatchRange(fact.startMs, fact.endMs).text;
  const window = pending ? formatMatchRange(pending.startMs, pending.endMs).text : '';
  const message = (() => {
    switch (status) {
      case 'resolving':
      case 'renewing':
      case 'notFound':
      case 'offline':
      case 'error':
        return t(`evidence.${status}`);
      case 'rateLimited':
        return playback.retryAfterSeconds === undefined
          ? t('evidence.rateLimitedNoDelay')
          : t('evidence.rateLimited', { seconds: playback.retryAfterSeconds });
      case 'playing':
        return playback.finished ? t('evidence.finished') : `${t('evidence.playing')} ${t(playback.insideFact ? 'evidence.insideFact' : 'evidence.context')}`;
      case 'preparing':
        return playback.automaticAttemptsExhausted
          ? t('evidence.preparingExhausted', { window })
          : t('evidence.preparing', { window, seconds: pending?.retryAfterSeconds ?? 5 });
      case 'partUnavailable':
        if (pending?.state === 'failed') return t('evidence.failed', { window, reason: pending.reason ?? '' });
        if (pending?.state === 'not-covered') return t('evidence.notCovered', { window });
        return t('evidence.unknownPart', { window, state: pending?.state ?? '' });
    }
  })();

  const canTryAgain =
    status === 'error' ||
    (status === 'rateLimited' && delayElapsed) ||
    (status === 'partUnavailable' && pending?.state === 'failed') ||
    playback.automaticAttemptsExhausted;

  return (
    <Dialog open onClose={onClose} aria-labelledby={titleId} fullWidth maxWidth="md">
      <DialogTitle id={titleId}>{t('evidence.title', { capability, range: factRange })}</DialogTitle>
      <DialogContent>
        <div role="status" aria-live="polite">
          <Typography>{message}</Typography>
          {playback.shortened && playback.coveredWindow && (
            <Typography>
              {t('evidence.shortened', { window: formatMatchRange(playback.coveredWindow.startMs, playback.coveredWindow.endMs).text })}
            </Typography>
          )}
        </div>
        {[0, 1].map((index) => (
          <video
            key={index}
            ref={videoRefs.current[index]}
            aria-label={t('evidence.videoLabel')}
            crossOrigin="anonymous"
            preload="auto"
            playsInline
            controls
            style={{ width: '100%', display: index === active && status === 'playing' && !playback.finished ? 'block' : 'none' }}
            onTimeUpdate={() => onTimeUpdate(index)}
            onEnded={() => index === active && advance()}
            onError={() => index === active && mediaFailed()}
          />
        ))}
      </DialogContent>
      <DialogActions>
        {status === 'partUnavailable' && (
          <Button onClick={playback.continueAfterUnavailable}>{t('evidence.continue')}</Button>
        )}
        {canTryAgain && <Button onClick={playback.tryAgain}>{t('evidence.tryAgain')}</Button>}
        <Button onClick={onClose}>{t('evidence.close')}</Button>
      </DialogActions>
    </Dialog>
  );
}
