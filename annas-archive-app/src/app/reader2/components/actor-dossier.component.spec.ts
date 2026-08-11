import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActorDossierComponent } from './actor-dossier.component';
import { Actor, ActorCorrection } from '../reader2.models';
import { actor, contents, group } from '../testing/cast';

/**
 * Who somebody is, and the reader's chance to correct it.
 *
 * <p>One component for both the cast list and the map. The edit form is a plain
 * form because nothing on it costs anything — no confirm dialog, no allowance,
 * no model call.</p>
 */
describe('ActorDossierComponent', () => {
  let fixture: ComponentFixture<ActorDossierComponent>;
  let emitted: ActorCorrection | undefined;
  let hidden: boolean | undefined;

  beforeEach(async () => {
    emitted = undefined;
    hidden = undefined;
    await TestBed.configureTestingModule({ imports: [ActorDossierComponent] }).compileComponents();
    fixture = TestBed.createComponent(ActorDossierComponent);
  });

  /**
   * Front matter first, so a chapter's index and its number disagree — which is
   * what they do in a real book, and what numbering them got wrong.
   */
  const CONTENTS = contents('Cover', 'Copyright', 'Chapter One', 'Chapter Two', 'Chapter Three');

  function render(who: Actor, others: Actor[] = []): HTMLElement {
    fixture.componentRef.setInput('actor', who);
    fixture.componentRef.setInput('groups', [group('g1', 'The Rostovs')]);
    fixture.componentRef.setInput('chapters', CONTENTS);
    fixture.componentRef.setInput('others', others);
    fixture.componentInstance.correct.subscribe(c => (emitted = c));
    fixture.componentInstance.hide.subscribe(h => (hidden = h));
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function press(label: string): void {
    Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'))
      .find(b => b.textContent?.trim() === label)!
      .click();
    fixture.detectChanges();
  }

  function type(selector: string, value: string): void {
    const field = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLInputElement | HTMLTextAreaElement>(selector)!;

    field.value = value;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  const PIERRE = actor('a1', 'Pyotr Bezukhov', 'Major', {
    dossier: 'An illegitimate son who inherits.',
    arc: [{ chapter: 3, change: 'inherits the estate' }],
    groupIds: ['g1']
  });

  it('shows what the model recorded', () => {
    const text = render(PIERRE).textContent ?? '';

    expect(text).toContain('An illegitimate son');
    expect(text).toContain('inherits the estate');
    expect(text).toContain('The Rostovs');
  });

  /**
   * Chapter indices count the spine, front matter included, so index 3 is the
   * book's second chapter. Numbering it put "Ch 4" beside a contents list
   * reading "Chapter Two" — two names for one chapter, on one screen.
   */
  it('names a chapter the way the contents list names it', () => {
    const text = render(PIERRE).textContent ?? '';

    expect(text).toContain('Chapter Two');
    expect(text).not.toContain('Ch 4');
  });

  it('falls back to counting when the contents list has not arrived', () => {
    fixture.componentRef.setInput('actor', PIERRE);
    fixture.componentRef.setInput('chapters', []);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Chapter 4');
  });

  /** A reader's note must never be mistaken for something the model wrote. */
  it('marks the reader’s own note as theirs', () => {
    const page = render(actor('a1', 'Pierre', 'Major', { readerNote: 'lied in ch 3' }));

    expect(page.querySelector('.note')?.textContent).toContain('lied in ch 3');
    expect(page.querySelector('.mine')?.textContent).toContain('Your note');
  });

  it('says so plainly when a name is all that is recorded', () => {
    expect(render(actor('a1', 'Yatras')).textContent).toContain('Nothing recorded about them yet');
  });

  // ─── correcting ─────────────────────────────────────────────────────

  it('emits a preferred name and a note together', () => {
    render(PIERRE);
    press('Edit');

    type('input[type="text"]', 'Pierre');
    type('textarea', 'the one who lied');
    (fixture.nativeElement as HTMLElement).querySelector('form')!
      .dispatchEvent(new Event('submit'));

    expect(emitted).toEqual({ preferredName: 'Pierre', note: 'the one who lied', sameAs: [] });
  });

  /**
   * An empty field is a cleared correction, not an empty one — the server drops
   * a correction that says nothing, so this is how an edit is undone.
   */
  it('emits nulls rather than empty strings, so clearing a field undoes it', () => {
    render(actor('a1', 'Pierre', 'Major', { readerNote: 'a note' }));
    press('Edit');

    type('textarea', '   ');
    (fixture.nativeElement as HTMLElement).querySelector('form')!
      .dispatchEvent(new Event('submit'));

    expect(emitted).toEqual({ preferredName: null, note: null, sameAs: [] });
  });

  it('starts the note field from what is already recorded, so editing is not retyping', () => {
    render(actor('a1', 'Pierre', 'Major', { readerNote: 'lied in ch 3' }));
    press('Edit');

    expect((fixture.nativeElement as HTMLElement).querySelector('textarea')!.value)
      .toBe('lied in ch 3');
  });

  it('offers the rest of the cast for “same person as”', () => {
    render(PIERRE, [actor('a2', 'Bezukhov'), actor('a3', 'Natasha')]);
    press('Edit');

    const options = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('option')).map(o => o.textContent?.trim());

    expect(options).toEqual(['Bezukhov', 'Natasha']);
  });

  it('offers no picker when there is nobody else to be', () => {
    render(PIERRE);
    press('Edit');

    expect((fixture.nativeElement as HTMLElement).querySelector('select')).toBeNull();
  });

  // ─── hiding ─────────────────────────────────────────────────────────

  /**
   * One press, not behind the edit form: hiding walk-ons is what a reader does
   * to twenty people in a row while reading the map, and making each one a form
   * to open, fill in and submit is making it not worth doing.
   */
  it('hides somebody in one press, without opening the form', () => {
    render(PIERRE);
    press('Hide from map');

    expect(hidden).toBeTrue();
    expect(emitted).toBeUndefined();
  });

  it('offers the way back for somebody already hidden', () => {
    render(actor('a1', 'Pierre', 'Major', { hidden: true }));
    press('Show on map');

    expect(hidden).toBeFalse();
  });

  /** Hidden is a state of the record, not merely an absence from a picture. */
  it('says plainly that somebody is hidden', () => {
    expect(render(actor('a1', 'Pierre', 'Major', { hidden: true })).textContent)
      .toContain('Hidden from the map');
  });

  it('emits nothing when the edit is cancelled', () => {
    render(PIERRE);
    press('Edit');
    type('input[type="text"]', 'Pierre');
    press('Cancel');

    expect(emitted).toBeUndefined();
    expect((fixture.nativeElement as HTMLElement).querySelector('form')).toBeNull();
  });
});
