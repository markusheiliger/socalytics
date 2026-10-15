import { appendSpan, createDefaultDraft, toTimelineSpans } from '../../../../../src/features/uploads/mappingDraft';
import { formatTime, parseTime } from '../../../../../src/features/uploads/mappingTime';

describe('mapping time input', () => {
  it('parses mm:ss and hh:mm:ss with millisecond precision', () => {
    expect(parseTime('00:00')).toBe(0);
    expect(parseTime('45:30')).toBe(2730);
    expect(parseTime('1:02:03')).toBe(3723);
    expect(parseTime('01:02:03.456')).toBe(3723.456);
    expect(parseTime(' 90:00 ')).toBe(5400);
  });

  it.each(['', '12', 'ab:cd', '1:75', '1:60:00', '1:2:3:4', '00:00.1234', '-1:00'])('rejects %j', (text) => {
    expect(parseTime(text)).toBeUndefined();
  });

  it('formats seconds back', () => {
    expect(formatTime(0)).toBe('00:00');
    expect(formatTime(2730)).toBe('45:30');
    expect(formatTime(3723.5)).toBe('1:02:03.5');
  });
});

describe('mapping draft', () => {
  it('proposes one span over the whole recording and leaves only the match time to enter', () => {
    const draft = createDefaultDraft(2700.25);
    expect(draft).toEqual([{ mediaStart: '00:00', mediaEnd: '45:00.25', matchStart: '' }]);
    expect(toTimelineSpans(draft).ok).toBe(false);
    expect(toTimelineSpans([{ ...draft[0], matchStart: '10:00' }])).toEqual({
      ok: true,
      spans: [{ mediaStartSeconds: 0, mediaEndSeconds: 2700.25, matchStartSeconds: 600 }],
    });
  });

  it('leaves the media end empty without a duration', () => {
    expect(createDefaultDraft()[0].mediaEnd).toBe('');
  });

  it('flags missing, invalid, and inverted values', () => {
    const result = toTimelineSpans([{ mediaStart: '10:00', mediaEnd: '05:00', matchStart: 'x' }]);
    expect(result).toEqual({ ok: false, errors: [{ mediaEnd: 'endBeforeStart', matchStart: 'invalidTime' }] });
  });

  it('appends a span that continues after the previous media end', () => {
    const spans = appendSpan(createDefaultDraft(100), 200);
    expect(spans[1]).toEqual({ mediaStart: '01:40', mediaEnd: '03:20', matchStart: '' });
  });
});
