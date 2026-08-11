import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BookmarkBarComponent } from './bookmark-bar.component';
import { Bookmark, ChapterInfo } from '../reader2.models';

function mark(id: string, chapter: number, wordOffset: number, label: string | null = null): Bookmark {
  return { id, chapter, wordOffset, label, createdAtUtc: '' };
}

const CHAPTERS: ChapterInfo[] = [
  { id: 0, title: 'Opening', level: 0, wordCount: 900, hasSummary: false, summaryIsStale: false },
  { id: 1, title: 'The turn', level: 0, wordCount: 900, hasSummary: false, summaryIsStale: false }
];

describe('BookmarkBarComponent', () => {
  let fixture: ComponentFixture<BookmarkBarComponent>;
  let component: BookmarkBarComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [BookmarkBarComponent] }).compileComponents();

    fixture = TestBed.createComponent(BookmarkBarComponent);
    component = fixture.componentInstance;
    component.chapters = CHAPTERS;
  });

  function render(bookmarks: Bookmark[], markHere: Bookmark | null = null, open = false): HTMLElement {
    fixture.componentRef.setInput('bookmarks', bookmarks);
    fixture.componentRef.setInput('markHere', markHere);
    fixture.componentRef.setInput('open', open);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  /**
   * One button whose meaning flips. If it ever rendered "add" while the page was
   * already marked, pressing it would delete a mark the reader meant to keep.
   */
  it('shows an empty marker when this page is not bookmarked', () => {
    const toggle = render([]).querySelector<HTMLButtonElement>('.toggle')!;

    expect(toggle.textContent).toContain('bookmark_border');
    expect(toggle.getAttribute('aria-pressed')).toBe('false');
  });

  it('shows a filled marker when this page is bookmarked', () => {
    const here = mark('a', 0, 300);
    const toggle = render([here], here).querySelector<HTMLButtonElement>('.toggle')!;

    expect(toggle.textContent).toContain('bookmark');
    expect(toggle.textContent).not.toContain('bookmark_border');
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
  });

  it('emits one toggle regardless of which way it currently points', () => {
    let toggles = 0;
    component.toggle.subscribe(() => { toggles++; });

    render([]).querySelector<HTMLButtonElement>('.toggle')!.click();

    expect(toggles).toBe(1);
  });

  it('keeps the list shut until it is opened, and never opens an empty one', () => {
    expect(render([mark('a', 0, 10)]).querySelector('.list')).toBeNull();
    expect(render([], null, true).querySelector('.list')).toBeNull();
    expect(render([mark('a', 0, 10)], null, true).querySelector('.list')).not.toBeNull();
  });

  it('names the chapter each mark is in', () => {
    const host = render([mark('a', 1, 10, 'here')], null, true);

    expect(host.querySelector('.where')?.textContent).toContain('The turn');
    expect(host.querySelector('.label')?.textContent).toContain('here');
  });

  /** A mark past the end of a truncated chapter list must still name something. */
  it('falls back to a chapter number when the chapter is not in the list', () => {
    const host = render([mark('a', 7, 10)], null, true);

    expect(host.querySelector('.where')?.textContent).toContain('Chapter 8');
  });

  it('emits the whole mark when one is chosen, so the caller need not look it up', () => {
    let jumped: Bookmark | null = null;
    component.jump.subscribe((m: Bookmark) => { jumped = m; });

    render([mark('a', 1, 250)], null, true).querySelector<HTMLButtonElement>('.jump')!.click();

    expect(jumped!).toEqual(mark('a', 1, 250));
  });

  it('emits only the id when a mark is removed', () => {
    let removed = '';
    component.remove.subscribe((id: string) => { removed = id; });

    render([mark('a', 1, 250)], null, true).querySelector<HTMLButtonElement>('.remove')!.click();

    expect(removed).toBe('a');
  });

  it('cannot be used while the reader is waiting', () => {
    component.disabled = true;

    expect(render([]).querySelector<HTMLButtonElement>('.toggle')!.disabled).toBeTrue();
  });
});
