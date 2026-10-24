import { MATCHES_KEY } from '../matches/matchKeys';

export function segmentsKey(matchId: string) {
  return [...MATCHES_KEY, 'segments', matchId];
}
