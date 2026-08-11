import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChapterLogComponent } from './chapter-log.component';
import { contents } from '../testing/cast';

/**
 * The one list behind an arc, a thread's beats, and a relationship's history.
 *
 * <p>All three were the same list — a chapter and a sentence — drawn three times
 * with three copies of the same CSS. They were collapsed when the chapter needed
 * naming rather than numbering, because that was a change that would otherwise
 * have had to be made, and got right, in three places.</p>
 */
describe('ChapterLogComponent', () => {
  let fixture: ComponentFixture<ChapterLogComponent>;

  const CONTENTS = contents('Cover', 'Copyright', 'Chapter One', 'Chapter Two');

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ChapterLogComponent] }).compileComponents();
    fixture = TestBed.createComponent(ChapterLogComponent);
  });

  function render(entries: { chapter: number; what: string }[], chapters = CONTENTS): HTMLElement {
    fixture.componentRef.setInput('entries', entries);
    fixture.componentRef.setInput('chapters', chapters);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('names each chapter and says what happened in it', () => {
    const rows = Array.from(
      render([
        { chapter: 2, what: 'she cuts him down from a tree' },
        { chapter: 3, what: 'they part at the pass' }
      ]).querySelectorAll('li'),
      row => row.textContent?.replace(/\s+/g, ' ').trim());

    expect(rows).toEqual([
      'Chapter One — she cuts him down from a tree',
      'Chapter Two — they part at the pass'
    ]);
  });

  it('keeps the order it is given, rather than sorting behind the caller', () => {
    const rows = Array.from(
      render([{ chapter: 3, what: 'later' }, { chapter: 2, what: 'earlier' }])
        .querySelectorAll('li'),
      row => row.textContent ?? '');

    expect(rows[0]).toContain('later');
  });

  it('draws nothing at all for an empty log', () => {
    expect(render([]).querySelectorAll('li').length).toBe(0);
  });
});
