import { formatCountdown, hawaiiSlotToUtcIso, secondsUntil } from './countdown.util';

describe('Date Night countdown utilities', () => {
  it('converts a Hawaii wall-clock slot to UTC instead of browser-local time', () => {
    expect(hawaiiSlotToUtcIso({ date: '2026-07-30', time: '16:30' }))
      .toBe('2026-07-31T02:30:00.000Z');
  });

  it('counts down against an absolute UTC instant', () => {
    spyOn(Date, 'now').and.returnValue(Date.parse('2026-07-31T02:29:30.000Z'));

    expect(secondsUntil('2026-07-31T02:30:00.000Z')).toBe(30);
    expect(formatCountdown(30)).toBe('00:30');
  });

  it('never reports a negative countdown', () => {
    spyOn(Date, 'now').and.returnValue(Date.parse('2026-07-31T03:31:00.000Z'));

    expect(secondsUntil('2026-07-31T02:30:00.000Z')).toBe(0);
    expect(formatCountdown(0)).toBe("Let's go!");
  });
});
