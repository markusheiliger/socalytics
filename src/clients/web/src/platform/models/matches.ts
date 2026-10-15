export type HomeAway = 'home' | 'away' | 'neutral';

export interface Match {
  id: string;
  teamId: string;
  opponentName: string;
  kickoffAt: Date;
  homeAway: HomeAway;
  competition?: string;
  createdAt: Date;
  version: number;
}

export interface MatchInput {
  opponentName: string;
  kickoffAt: Date;
  homeAway: HomeAway;
  competition?: string;
}

/** The opponent is fixed at creation (FR-002). */
export interface MatchUpdate {
  kickoffAt: Date;
  homeAway: HomeAway;
  competition?: string;
}

export interface PageRequest {
  continuationToken?: string;
  pageSize?: number;
}

export interface Page<T> {
  items: T[];
  continuationToken?: string;
}
