export const maxPartsAhead = 32;
export const renewWithinMs = 60_000;

export interface StoredGrant {
  readonly partNumber: number;
  readonly url: string;
  readonly requiredHeaders: Readonly<Record<string, string>>;
  readonly expiresAt: string;
}

// Grants live only in memory (FR-014).
export class GrantScheduler {
  private readonly grants = new Map<number, StoredGrant>();

  constructor(private readonly now: () => number = Date.now) {}

  store(grants: readonly StoredGrant[]): void {
    for (const grant of grants) {
      this.grants.set(grant.partNumber, grant);
    }
  }

  invalidate(partNumber: number): void {
    this.grants.delete(partNumber);
  }

  clear(): void {
    this.grants.clear();
  }

  // A grant that expires within the renewal window is not usable for a new transfer.
  usable(partNumber: number): StoredGrant | undefined {
    const grant = this.grants.get(partNumber);
    return grant && Date.parse(grant.expiresAt) - this.now() > renewWithinMs ? grant : undefined;
  }

  // `pending` lists the part numbers that still have to be sent, in transfer order.
  partsNeeded(pending: readonly number[]): number[] {
    return pending.slice(0, maxPartsAhead).filter((partNumber) => !this.usable(partNumber));
  }
}
