import { DateNightScheduleFormComponent } from './date-night-schedule-form.component';

describe('DateNightScheduleFormComponent', () => {
  it('offers every combination selected by the initial proposer', () => {
    const component = new DateNightScheduleFormComponent();
    const emitted = jasmine.createSpy('submitted');
    component.submitted.subscribe(emitted);

    component.selectDay(component.dayOptions[1]);
    component.selectDay(component.dayOptions[2]);
    component.selectTime('18:00');
    component.selectTime('19:30');
    component.submit();

    const firstDate = component.dayOptions[1].date;
    const secondDate = component.dayOptions[2].date;
    expect(emitted).toHaveBeenCalledOnceWith([
      { date: firstDate, time: '18:00' },
      { date: firstDate, time: '19:30' },
      { date: secondDate, time: '18:00' },
      { date: secondDate, time: '19:30' }
    ]);
  });

  it('allows the initial proposer to offer many dates', () => {
    const component = new DateNightScheduleFormComponent();

    component.dayOptions.slice(1, 5).forEach(day => component.selectDay(day));

    expect(component.selectedDates.size).toBe(4);
    expect(component.selectedDates.has(component.dayOptions[4].date)).toBeTrue();
  });

  it('limits a real cycle to the remaining days in its Monday–Sunday week', () => {
    const component = new DateNightScheduleFormComponent();
    const hawaiiToday = new Intl.DateTimeFormat('en-CA', {
      timeZone: 'Pacific/Honolulu', year: 'numeric', month: '2-digit', day: '2-digit'
    }).format(new Date());
    const today = new Date(`${hawaiiToday}T12:00:00Z`);
    const daysSinceMonday = (today.getUTCDay() + 6) % 7;
    const monday = new Date(today);
    monday.setUTCDate(monday.getUTCDate() - daysSinceMonday);
    const sunday = new Date(monday);
    sunday.setUTCDate(sunday.getUTCDate() + 6);

    component.cycleId = monday.toISOString().slice(0, 10);

    expect(component.dayOptions.length).toBe(7 - daysSinceMonday);
    expect(component.dayOptions[0].date).toBe(hawaiiToday);
    expect(component.dayOptions.at(-1)?.date).toBe(sunday.toISOString().slice(0, 10));
  });

  it('keeps a rolling seven-day window for the isolated dry run', () => {
    const component = new DateNightScheduleFormComponent();

    component.cycleId = 'test';

    expect(component.dayOptions.length).toBe(7);
  });

  it('emits one explicit slot in counter-proposal mode', () => {
    const component = new DateNightScheduleFormComponent();
    const emitted = jasmine.createSpy('submitted');
    component.multiple = false;
    component.submitted.subscribe(emitted);

    component.selectDay(component.dayOptions[1]);
    component.selectDay(component.dayOptions[2]);
    component.selectTime('18:00');
    component.selectTime('20:00');
    component.submit();

    expect(emitted).toHaveBeenCalledOnceWith([
      { date: component.dayOptions[2].date, time: '20:00' }
    ]);
  });
});
