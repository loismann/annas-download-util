import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CharacterTableComponent } from './character-table.component';
import { Actor } from '../reader2.models';
import { actor, group } from '../testing/cast';

/**
 * The cast list.
 *
 * <p>It filters nothing — that moved to `cast-filter.ts` when the map needed the
 * same answer, and its tests moved with it. What is left to assert here is a
 * row, a dossier, and the difference between "hidden" and "nobody".</p>
 */
describe('CharacterTableComponent', () => {
  let fixture: ComponentFixture<CharacterTableComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [CharacterTableComponent] }).compileComponents();
    fixture = TestBed.createComponent(CharacterTableComponent);
  });

  function render(actors: Actor[], anybody = true): HTMLElement {
    fixture.componentRef.setInput('actors', actors);
    fixture.componentRef.setInput('vocabulary',
      { actors: 'Characters', groups: 'Factions', threads: 'Plot threads' });
    fixture.componentRef.setInput('groups', [group('g1', 'The Rostovs')]);
    fixture.componentRef.setInput('anybody', anybody);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function rows(): number {
    return (fixture.nativeElement as HTMLElement).querySelectorAll('.actor').length;
  }

  it('lists everybody it is given', () => {
    render([actor('a1', 'Pierre', 'Major'), actor('a2', 'Natasha', 'Major')]);

    expect(rows()).toBe(2);
  });

  it('lists the other names beside the canonical one', () => {
    const page = render([actor('a1', 'Pyotr Bezukhov', 'Major', { aliases: ['Pierre'] })]);

    expect(page.querySelector('.aliases')?.textContent).toContain('Pierre');
  });

  it('opens a dossier with the arc on a click', () => {
    const page = render([actor('a1', 'Pierre', 'Major', {
      dossier: 'An illegitimate son who inherits.',
      arc: [{ chapter: 3, change: 'inherits the estate' }],
      groupIds: ['g1']
    })]);

    page.querySelector<HTMLButtonElement>('.row')!.click();
    fixture.detectChanges();

    const dossier = page.querySelector('.dossier')?.textContent ?? '';

    expect(dossier).toContain('An illegitimate son');
    expect(dossier).toContain('inherits the estate');
    expect(dossier).toContain('The Rostovs');
  });

  /**
   * The two empty states are different sentences, and telling them apart is the
   * whole reason the panel passes down whether there is anybody at all: "nothing
   * matches" sends the reader to the filters, "nobody recorded" sends them to
   * summarise a chapter.
   */
  it('says when the filters have hidden everything, rather than going blank', () => {
    expect(render([], true).querySelector('.empty')?.textContent)
      .toContain('Nothing matches those filters');
  });

  it('says when there is genuinely nobody yet', () => {
    expect(render([], false).querySelector('.empty')?.textContent)
      .toContain('No characters recorded yet');
  });
});
