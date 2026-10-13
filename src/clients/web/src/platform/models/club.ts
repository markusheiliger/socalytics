export interface Club {
  id: string;
  displayName: string;
  version: number;
}

export type SeasonState = 'draft' | 'active' | 'archived';

export interface Season {
  id: string;
  name: string;
  state: SeasonState;
  createdAt: Date;
  activatedAt?: Date;
  archivedAt?: Date;
  version: number;
}
