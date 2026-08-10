import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CharacterTableComponent } from './character-table.component';
import { Actor, ActorTier } from '../reader2.models';
import { actor, group, thread } from '../testing/cast';

/** A long novel's worth: 20 majors, 80 secondary, 400 walk-ons. */
function bigCast(): Actor[] {
  const tier = (i: number): ActorTier => i < 20 ? 'Major' : i < 100 ? 'Secondary' : 'Minor';

  return Array.from({ length: 500 }, (_, i) =>
    actor(`a${i}`, `Person ${i}`, tier(i), {
      groupIds: i % 2 === 0 ? ['g1'] : [],
      lastSeenChapter: i % 10
    }));
}

describe('CharacterTableComponent', () => {
  let fixture: ComponentFixture<CharacterTableComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [CharacterTableComponent] }).compileComponents();
    fixture = TestBed.createComponent(CharacterTableComponent);
  });

  function render(actors: Actor[]): HTMLElement {
    fixture.componentRef.setInput('actors', actors);
    fixture.componentRef.setInput('vocabulary',
      { actors: 'Characters', groups: 'Factions', threads: 'Plot threads' });
    fixture.componentRef.setInput('groups', [group('g1', 'The Rostovs')]);
    fixture.componentRef.setInput('threads', [thread('t1', 'The duel', 'Active', { participantIds: ['a0', 'a1'] })]);
    fixture.componentRef.setInput('currentChapter', 5);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function shown(): number {
    return (fixture.nativeElement as HTMLElement).querySelectorAll('.actor').length;
  }

  function press(label: string): void {
    const buttons = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'));

    buttons.find(b => b.textContent?.trim().startsWith(label))!.click();
    fixture.detectChanges();
  }

  /** The whole reason for the default: 500 entries is the wall of names, not the cure. */
  it('opens at 500 actors showing major and secondary, and counts the hidden', () => {
    const page = render(bigCast());

    expect(shown()).toBe(100);
    expect(page.querySelector('.hidden-note')?.textContent).toContain('400 not shown');
  });

  it('shows everybody on one press, at 500 actors', () => {
    render(bigCast());
    press('Show everybody');

    expect(shown()).toBe(500);
  });

  it('filters by group', () => {
    render(bigCast());

    const select = (fixture.nativeElement as HTMLElement).querySelectorAll('select')[0];
    select.value = 'g1';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(shown()).toBe(50);
  });

  it('filters to the participants of one thread', () => {
    render(bigCast());

    const select = (fixture.nativeElement as HTMLElement).querySelectorAll('select')[1];
    select.value = 't1';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(shown()).toBe(2);
  });

  it('filters to who was last seen in this chapter', () => {
    render(bigCast());
    press('In this chapter');

    // Of the 100 defaults, those with lastSeenChapter === 5.
    expect(shown()).toBe(10);
  });

  it('narrows by tier one chip at a time', () => {
    render(bigCast());
    press('Secondary');

    expect(shown()).toBe(20);
  });

  it('says when the filters have hidden everything, rather than going blank', () => {
    render([actor('a1', 'Pierre', 'Minor')]);

    expect((fixture.nativeElement as HTMLElement).querySelector('.empty')?.textContent)
      .toContain('filters');
  });

  it('opens a dossier with the arc on a click', () => {
    render([actor('a1', 'Pierre', 'Major', {
      dossier: 'Illegitimate son of a count.',
      arc: [{ chapter: 2, change: 'Inherits everything.' }]
    })]);
    press('Pierre');

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('.dossier')?.textContent).toContain('Illegitimate son');
    expect(page.querySelector('.arc')?.textContent).toContain('Ch 3');
  });

  it('lists the other names beside the canonical one', () => {
    const page = render([actor('a1', 'Pierre', 'Major', { aliases: ['Pyotr Kirillovich'] })]);

    expect(page.querySelector('.aliases')?.textContent).toContain('Pyotr Kirillovich');
  });
});
