import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CastFilterComponent } from './cast-filter.component';
import { CastFilter, DEFAULT_FILTER } from '../services/cast-filter';
import { group, thread } from '../testing/cast';

describe('CastFilterComponent', () => {
  let fixture: ComponentFixture<CastFilterComponent>;
  let emitted: CastFilter | undefined;

  beforeEach(async () => {
    emitted = undefined;
    await TestBed.configureTestingModule({ imports: [CastFilterComponent] }).compileComponents();
    fixture = TestBed.createComponent(CastFilterComponent);
  });

  /**
   * @param notShown How many the filter is keeping back — a different thing from
   *   `hiddenCount`, which is how many the reader has hidden from the map. The
   *   two used to share the word "hidden" on this component and its inputs, which
   *   is exactly the kind of collision that ends up wiring one to the other.
   */
  function render(
    notShown = 0, filter: CastFilter = DEFAULT_FILTER, hiddenCount = 0
  ): HTMLElement {
    fixture.componentRef.setInput('filter', filter);
    fixture.componentRef.setInput('vocabulary',
      { actors: 'Characters', groups: 'Factions', threads: 'Plot threads' });
    fixture.componentRef.setInput('groups', [group('g1', 'The Rostovs')]);
    fixture.componentRef.setInput('threads', [thread('t1', 'The duel')]);
    fixture.componentRef.setInput('notShown', notShown);
    fixture.componentRef.setInput('hiddenCount', hiddenCount);
    fixture.componentInstance.change.subscribe(f => (emitted = f));
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function press(label: string): void {
    Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'))
      .find(b => b.textContent?.trim().startsWith(label))!
      .click();
    fixture.detectChanges();
  }

  it('names the book type’s own words for its groupings', () => {
    const page = render();

    expect(page.textContent).toContain('All factions');
    expect(page.textContent).toContain('All plot threads');
  });

  /** The count is what tells somebody looking for a walk-on there is a control. */
  it('says how many the filter is keeping back, and offers the one press that shows them', () => {
    const page = render(400);

    expect(page.querySelector('.hidden-note')?.textContent).toContain('400 not shown');

    press('Show everybody');

    expect(emitted!.tiers.length).toBe(4);
  });

  it('says nothing about people held back when none are', () => {
    expect(render(0).querySelector('.hidden-note')).toBeNull();
  });

  // ─── reviewing what the reader has hidden ───────────────────────────

  /**
   * The door back. Somebody hidden is off the map and below the default tiers,
   * so without this there is no way to reach them — which would make hiding a
   * deletion that lied about being one.
   */
  it('offers a way back to whoever the reader has hidden', () => {
    const page = render(0, DEFAULT_FILTER, 3);

    expect(page.querySelector('.hidden-chip')?.textContent).toContain('3');

    press('Hidden from map');

    expect(emitted!.hiddenOnly).toBeTrue();
  });

  it('offers nothing to review when the reader has hidden nobody', () => {
    expect(render(0, DEFAULT_FILTER, 0).querySelector('.hidden-chip')).toBeNull();
  });

  it('says what it is showing while reviewing, and how to stop', () => {
    const page = render(0, { ...DEFAULT_FILTER, hiddenOnly: true }, 3);

    expect(page.querySelector('.hidden-note')?.textContent).toContain('hidden from the map');

    press('Done');

    expect(emitted!.hiddenOnly).toBeFalse();
  });

  it('emits a tier turned off without touching the rest of the filter', () => {
    render(0, { ...DEFAULT_FILTER, groupId: 'g1' });

    press('Major');

    expect(emitted!.tiers).toEqual(['Secondary']);
    expect(emitted!.groupId).toBe('g1', 'narrowing by tier must not widen the faction');
  });

  it('emits the chapter filter as a toggle', () => {
    render();

    press('In this chapter');

    expect(emitted!.hereOnly).toBeTrue();
  });

  /** It holds nothing: the panel owns what is selected, because the map needs it too. */
  it('renders what it is given rather than what it was last told', () => {
    const page = render(0, { ...DEFAULT_FILTER, tiers: ['Minor'] });
    const on = Array.from(page.querySelectorAll('.chip.on')).map(b => b.textContent?.trim());

    expect(on).toEqual(['Minor']);
  });
});
