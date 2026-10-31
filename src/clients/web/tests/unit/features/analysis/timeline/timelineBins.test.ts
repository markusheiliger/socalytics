import {
  BINS_PER_VIEW,
  ZOOM_LEVELS,
  clampExtentStart,
  defaultZoom,
  deriveBins,
  moveExtentStart,
  overallExtent,
  zoomLevel,
  type TimelineFact,
} from '../../../../../src/features/analysis/timeline/timelineBins';

const fine = zoomLevel('10s');
const minute = zoomLevel('1m');
const coarse = zoomLevel('5m');

function win(startMs: number, endMs: number, state = 'accepted') {
  return { startMs, endMs, state };
}

function derive(facts: TimelineFact[], windows = [win(0, 300_000)], partial = false, extentStartMs = 0, zoom = fine) {
  return deriveBins({ windows, facts, partial, extentStartMs, zoom });
}

describe('zoom levels', () => {
  it('has four fixed levels with 30 bins each', () => {
    expect(ZOOM_LEVELS.map((level) => [level.id, level.binMs, level.bins])).toEqual([
      ['10s', 10_000, BINS_PER_VIEW],
      ['30s', 30_000, BINS_PER_VIEW],
      ['1m', 60_000, BINS_PER_VIEW],
      ['5m', 300_000, BINS_PER_VIEW],
    ]);
    expect(BINS_PER_VIEW).toBe(30);
  });
});

describe('overallExtent', () => {
  it('is undefined without windows and facts', () => {
    expect(overallExtent([])).toBeUndefined();
    expect(overallExtent([{ windows: [], facts: [] }])).toBeUndefined();
  });

  it('unites windows and facts of all lanes and rounds out to the finest bin size', () => {
    const extent = overallExtent([
      { windows: [win(12_000, 65_000)], facts: [] },
      { windows: [], facts: [{ startMs: 3_456, endMs: 4_000 }] },
    ]);
    expect(extent).toEqual({ startMs: 0, endMs: 70_000 });
  });

  it('keeps a zero-length fact inside the extent', () => {
    expect(overallExtent([{ windows: [], facts: [{ startMs: 20_000, endMs: 20_000 }] }])).toEqual({
      startMs: 20_000,
      endMs: 30_000,
    });
  });

  it('does not extend beyond already aligned values', () => {
    expect(overallExtent([{ windows: [win(10_000, 40_000)], facts: [] }])).toEqual({ startMs: 10_000, endMs: 40_000 });
  });
});

describe('defaultZoom', () => {
  it('is 5 m when the extent is unavailable', () => {
    expect(defaultZoom(undefined)).toBe('5m');
  });

  it('is the finest level whose bins cover the extent', () => {
    expect(defaultZoom({ startMs: 0, endMs: 300_000 })).toBe('10s');
    expect(defaultZoom({ startMs: 0, endMs: 310_000 })).toBe('30s');
    expect(defaultZoom({ startMs: 0, endMs: 900_000 })).toBe('30s');
    expect(defaultZoom({ startMs: 0, endMs: 910_000 })).toBe('1m');
    expect(defaultZoom({ startMs: 0, endMs: 1_800_000 })).toBe('1m');
    expect(defaultZoom({ startMs: 0, endMs: 1_810_000 })).toBe('5m');
  });

  it('falls back to 5 m when no level covers the extent', () => {
    expect(defaultZoom({ startMs: 0, endMs: 20_000_000 })).toBe('5m');
  });

  it('measures the length of the extent, not its end', () => {
    expect(defaultZoom({ startMs: 3_600_000, endMs: 3_900_000 })).toBe('10s');
  });

  it('is a pure function of the extent, so a chosen level is kept by the caller across later loads', () => {
    const first = defaultZoom({ startMs: 0, endMs: 300_000 });
    const chosen = first;
    defaultZoom({ startMs: 0, endMs: 3_000_000 });
    expect(chosen).toBe('10s');
  });
});

describe('clampExtentStart', () => {
  const extent = { startMs: 0, endMs: 600_000 };

  it('floors to a multiple of the bin size', () => {
    expect(clampExtentStart(123_456, fine, extent)).toBe(120_000);
  });

  it('clamps to the extent start and to the end minus the visible width', () => {
    expect(clampExtentStart(-5_000, fine, extent)).toBe(0);
    expect(clampExtentStart(999_999, fine, extent)).toBe(300_000);
  });

  it('never goes below the floored extent start', () => {
    expect(clampExtentStart(5_000, fine, { startMs: 10_000, endMs: 610_000 })).toBe(10_000);
    expect(clampExtentStart(5_000, fine, { startMs: 10_000.9, endMs: 610_000 })).toBe(10_000);
  });

  it('uses the extent start when the extent is shorter than the view', () => {
    expect(clampExtentStart(50_000, fine, { startMs: 0, endMs: 100_000 })).toBe(0);
  });
});

describe('moveExtentStart', () => {
  const extent = { startMs: 0, endMs: 3_600_000 };

  it('moves by half the visible width and stays inside the extent', () => {
    expect(moveExtentStart(0, 1, minute, extent)).toBe(900_000);
    expect(moveExtentStart(900_000, 1, minute, extent)).toBe(1_800_000);
    expect(moveExtentStart(1_800_000, 1, minute, extent)).toBe(1_800_000);
    expect(moveExtentStart(900_000, -1, minute, extent)).toBe(0);
    expect(moveExtentStart(0, -1, minute, extent)).toBe(0);
  });
});

describe('deriveBins', () => {
  it('returns one bin per zoom bin with contiguous bounds', () => {
    const bins = derive([]);
    expect(bins).toHaveLength(30);
    expect(bins[0]).toMatchObject({ startMs: 0, endMs: 10_000 });
    expect(bins[29]).toMatchObject({ startMs: 290_000, endMs: 300_000 });
  });

  it('counts a fact once in every bin it overlaps', () => {
    const bins = derive([{ startMs: 5_000, endMs: 25_000 }]);
    expect(bins.slice(0, 4).map((bin) => bin.count)).toEqual([1, 1, 1, 0]);
  });

  it('treats the end of a fact as exclusive', () => {
    const bins = derive([{ startMs: 10_000, endMs: 20_000 }]);
    expect(bins.slice(0, 3).map((bin) => bin.count)).toEqual([0, 1, 0]);
  });

  it('counts identical and overlapping facts separately', () => {
    const bins = derive([
      { startMs: 10_000, endMs: 15_000 },
      { startMs: 10_000, endMs: 15_000 },
      { startMs: 12_000, endMs: 22_000 },
    ]);
    expect(bins.slice(0, 3).map((bin) => bin.count)).toEqual([0, 3, 1]);
  });

  it('counts a zero-length fact in the bin that contains its start', () => {
    expect(derive([{ startMs: 10_000, endMs: 10_000 }]).slice(0, 3).map((bin) => bin.count)).toEqual([0, 1, 0]);
    expect(derive([{ startMs: 9_999, endMs: 9_999 }]).slice(0, 3).map((bin) => bin.count)).toEqual([1, 0, 0]);
  });

  it('ignores facts outside the visible extent and clips facts that reach into it', () => {
    const bins = derive([
      { startMs: 400_000, endMs: 410_000 },
      { startMs: 400_000, endMs: 400_000 },
      { startMs: -50_000, endMs: -40_000 },
      { startMs: -50_000, endMs: 5_000 },
      { startMs: 290_000, endMs: 500_000 },
    ]);
    expect(bins[0].count).toBe(1);
    expect(bins[29].count).toBe(1);
    expect(bins.reduce((sum, bin) => sum + bin.count, 0)).toBe(2);
  });

  it('derives bins from a moved extent start', () => {
    const bins = derive([{ startMs: 125_000, endMs: 125_000 }], [win(0, 600_000)], false, 120_000);
    expect(bins[0].startMs).toBe(120_000);
    expect(bins[0].count).toBe(1);
  });

  it('shows bins beyond 90 minutes for extra time', () => {
    const windows = [win(0, 7_200_000)];
    const bins = derive([{ startMs: 5_700_000, endMs: 5_700_000 }], windows, false, 5_400_000, minute);
    expect(bins[5]).toMatchObject({ startMs: 5_700_000, count: 1, state: 'facts', coverage: 'accepted' });
    expect(bins[29].endMs).toBe(7_200_000);
    expect(bins[29]).toMatchObject({ state: 'analysedNoFacts', count: 0 });

    const coarseBins = derive([{ startMs: 6_000_000, endMs: 6_000_000 }], windows, false, 0, coarse);
    expect(coarseBins[20]).toMatchObject({ startMs: 6_000_000, count: 1 });
  });

  it('keeps the sum of the bins equal to the overlapping facts counted per bin (SC-002)', () => {
    const facts: TimelineFact[] = [];
    for (let index = 0; index < 150; index++) {
      const startMs = (index * 7_919) % 320_000;
      facts.push({ startMs, endMs: startMs + (index % 5) * 6_500 });
    }
    const bins = derive(facts);
    const expected = facts.reduce((sum, fact) => {
      const overlapping = bins.filter((bin) =>
        fact.endMs > fact.startMs
          ? fact.startMs < bin.endMs && fact.endMs > bin.startMs
          : fact.startMs >= bin.startMs && fact.startMs < bin.endMs,
      );
      return sum + overlapping.length;
    }, 0);
    expect(bins.reduce((sum, bin) => sum + bin.count, 0)).toBe(expected);
  });

  describe('coverage', () => {
    it('is accepted when accepted windows cover the whole bin, also through several windows', () => {
      const bins = derive([], [win(0, 6_000), win(4_000, 10_000), win(10_000, 20_000)]);
      expect(bins[0]).toMatchObject({ coverage: 'accepted', state: 'analysedNoFacts' });
      expect(bins[1]).toMatchObject({ coverage: 'accepted', state: 'analysedNoFacts' });
      expect(bins[2]).toMatchObject({ coverage: 'none', state: 'noCoverage' });
    });

    it('is partial when accepted windows cover only part of the bin', () => {
      const bins = derive([], [win(0, 15_000)]);
      expect(bins[0].coverage).toBe('accepted');
      expect(bins[1]).toMatchObject({ coverage: 'partial', state: 'partial', count: 0 });
    });

    it('is partial when accepted and not analysed windows share a bin', () => {
      const bins = derive([], [win(0, 5_000), win(5_000, 10_000, 'missing')]);
      expect(bins[0]).toMatchObject({ coverage: 'partial', state: 'partial' });
    });

    it('is not analysed when only windows of other states cover the bin', () => {
      for (const state of ['missing', 'failed', 'blocked', 'skipped']) {
        const bins = derive([], [win(0, 10_000, state)]);
        expect(bins[0]).toMatchObject({ coverage: 'notAnalysed', state: 'notAnalysed', rawStates: [] });
      }
    });

    it('is none without a window, and a fact there still shows as facts', () => {
      const bins = derive([{ startMs: 1_000, endMs: 2_000 }], []);
      expect(bins[0]).toMatchObject({ coverage: 'none', state: 'facts', count: 1 });
      expect(bins[1]).toMatchObject({ coverage: 'none', state: 'noCoverage', count: 0 });
    });

    it('matches the accepted state case-insensitively', () => {
      expect(derive([], [win(0, 10_000, 'Accepted')])[0].coverage).toBe('accepted');
    });

    it('keeps unknown states raw, treats them as not analysed, and never shows them as zero', () => {
      const bins = derive([], [win(0, 10_000, 'Quarantined'), win(10_000, 20_000, 'accepted'), win(10_000, 20_000, 'Odd')]);
      expect(bins[0]).toMatchObject({ coverage: 'notAnalysed', state: 'notAnalysed', rawStates: ['Quarantined'] });
      expect(bins[1]).toMatchObject({ coverage: 'accepted', state: 'analysedNoFacts', rawStates: ['Odd'] });

      const mixed = derive([], [win(0, 5_000), win(5_000, 10_000, 'Quarantined')]);
      expect(mixed[0]).toMatchObject({ coverage: 'partial', state: 'partial', rawStates: ['Quarantined'] });
    });

    it('never reports analysed without facts outside fully accepted bins', () => {
      const windows = [win(0, 40_000), win(40_000, 80_000, 'failed'), win(80_000, 95_000), win(100_000, 120_000, 'Odd')];
      for (const partial of [false, true]) {
        for (const bin of derive([{ startMs: 2_000, endMs: 3_000 }], windows, partial)) {
          if (bin.state === 'analysedNoFacts') {
            expect(bin.coverage).toBe('accepted');
            expect(bin.count).toBe(0);
          }
        }
      }
    });
  });

  describe('partial lanes', () => {
    const facts = [
      { startMs: 5_000, endMs: 8_000 },
      { startMs: 25_000, endMs: 25_000 },
    ];

    it('marks count 0 bins after the loaded horizon as not loaded and keeps earlier ones analysed without facts', () => {
      const bins = derive(facts, [win(0, 300_000)], true);
      expect(bins.map((bin) => bin.state).slice(0, 5)).toEqual([
        'facts',
        'analysedNoFacts',
        'facts',
        'notLoaded',
        'notLoaded',
      ]);
      expect(bins[29].state).toBe('notLoaded');
    });

    it('keeps count 0 bins after the last fact analysed without facts in a non-partial lane', () => {
      const bins = derive(facts, [win(0, 300_000)], false);
      expect(bins[3].state).toBe('analysedNoFacts');
      expect(bins[29].state).toBe('analysedNoFacts');
    });

    it('does not turn partially or not covered bins into not loaded', () => {
      const bins = derive(facts, [win(0, 35_000), win(40_000, 60_000, 'failed')], true);
      expect(bins[3].state).toBe('partial');
      expect(bins[4].state).toBe('notAnalysed');
      expect(bins[10].state).toBe('noCoverage');
    });
  });
});
