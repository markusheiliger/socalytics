import { CURRENT_STATES, ENDED_STATES, type Registration, type RegistrationState } from '../../platform/models/registrations';

type KnownState = Exclude<RegistrationState, 'unknown'>;

/** `current` (default), `ended`, `all`, or one registration state (research.md § R3). */
export type FleetFilter = 'current' | 'ended' | 'all' | KnownState;

export const DEFAULT_FLEET_FILTER: FleetFilter = 'current';
export const STATE_FILTERS: readonly KnownState[] = [...CURRENT_STATES, ...ENDED_STATES].filter(
  (s): s is KnownState => s !== 'unknown',
);
export const FLEET_FILTERS: readonly FleetFilter[] = ['current', 'ended', 'all', ...STATE_FILTERS];

export const isFleetFilter = (value: string): value is FleetFilter => (FLEET_FILTERS as readonly string[]).includes(value);

/** Only a single state uses the API's `state` filter; the other filters read the unfiltered list. */
export const fleetQueryState = (filter: FleetFilter): RegistrationState | undefined =>
  filter === 'current' || filter === 'ended' || filter === 'all' ? undefined : filter;

export function filterFleet(items: readonly Registration[], filter: FleetFilter): Registration[] {
  switch (filter) {
    case 'all':
      return [...items];
    case 'current':
      return items.filter((r) => CURRENT_STATES.includes(r.state));
    case 'ended':
      return items.filter((r) => ENDED_STATES.includes(r.state));
    default:
      return items.filter((r) => r.state === filter);
  }
}

const label = (r: Registration) => r.deviceMetadata.hostLabel?.trim().toLowerCase() || undefined;

/** Pending requests first, then host label (case-insensitive, missing last), then `createdAt`. */
export function sortFleet(items: readonly Registration[]): Registration[] {
  return [...items].sort((a, b) => {
    const pending = Number(b.state === 'pendingApproval') - Number(a.state === 'pendingApproval');
    if (pending !== 0) return pending;
    const la = label(a);
    const lb = label(b);
    if (la !== lb) {
      if (la === undefined) return 1;
      if (lb === undefined) return -1;
      const byLabel = la < lb ? -1 : 1;
      return byLabel;
    }
    const ta = a.createdAt?.getTime() ?? Number.POSITIVE_INFINITY;
    const tb = b.createdAt?.getTime() ?? Number.POSITIVE_INFINITY;
    if (ta !== tb) return ta < tb ? -1 : 1;
    return a.registrationId < b.registrationId ? -1 : a.registrationId > b.registrationId ? 1 : 0;
  });
}

export const arrangeFleet = (items: readonly Registration[], filter: FleetFilter) => sortFleet(filterFleet(items, filter));
