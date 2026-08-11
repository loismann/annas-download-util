import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BookShelfComponent } from './book-shelf.component';
import { Book, Lens } from '../reader2.models';

function book(id: string, extra: Partial<Book> = {}): Book {
  return {
    bookId: id, fileName: `${id}.epub`, title: `Book ${id}`, authors: ['An Author'],
    lensKey: 'ideas-key', addedAtUtc: '', lastOpenedAtUtc: null, isAvailable: true,
    coverUrl: null, ...extra
  };
}

const LENSES: Lens[] = [{
  key: 'ideas-key', displayName: 'Ideas', description: '', icon: 'psychology',
  sortOrder: 0, isDefault: true, buildsStoryModel: false, storyVocabulary: null
}];

describe('BookShelfComponent', () => {
  let fixture: ComponentFixture<BookShelfComponent>;
  let component: BookShelfComponent;

  beforeEach(async () => {
    // A router, because the way to the library is a real routerLink and an
    // unresolved one renders no href at all.
    await TestBed.configureTestingModule({
      imports: [BookShelfComponent],
      providers: [provideRouter([])]
    }).compileComponents();

    fixture = TestBed.createComponent(BookShelfComponent);
    component = fixture.componentInstance;
  });

  /**
   * setInput rather than assignment: these components are OnPush, and a plain
   * assignment leaves the view clean, so a second render inside one spec would
   * silently show the first render's markup.
   */
  function render(books: Book[], lenses = LENSES): HTMLElement {
    fixture.componentRef.setInput('books', books);
    fixture.componentRef.setInput('lenses', lenses);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('lists the books it is given, in the order it is given them', () => {
    const titles = Array.from(render([book('a'), book('b')]).querySelectorAll('.title'));

    expect(titles.map(t => t.textContent?.trim())).toEqual(['Book a', 'Book b']);
  });

  /**
   * The file went missing but every summary the household paid for is still
   * there, and the hash will re-locate the file if it comes back. Hiding the
   * book would look like the work had been lost.
   */
  it('shows a book whose file is missing, says so, and does not let it be opened', () => {
    const host = render([book('gone', { isAvailable: false })]);

    expect(host.querySelector('.missing')?.textContent).toContain('missing');
    expect(host.querySelector<HTMLButtonElement>('.book')!.disabled).toBeTrue();
    expect(host.querySelector('li')!.classList).toContain('unavailable');
  });

  it('does not emit when an unavailable book is clicked', () => {
    let opened: string | null = null;
    component.open.subscribe((id: string) => { opened = id; });

    render([book('gone', { isAvailable: false })])
      .querySelector<HTMLButtonElement>('.book')!.click();

    expect(opened).toBeNull();
  });

  it('emits the book id when an available book is chosen', () => {
    let opened = '';
    component.open.subscribe((id: string) => { opened = id; });

    render([book('here')]).querySelector<HTMLButtonElement>('.book')!.click();

    expect(opened).toBe('here');
  });

  /** The server names book types; the shelf never invents one of its own. */
  it('shows the server’s name for a book type', () => {
    expect(render([book('a')]).querySelector('.type')?.textContent?.trim()).toBe('Ideas');
  });

  it('falls back to the raw key for a type this build no longer has', () => {
    const host = render([book('a', { lensKey: 'retired-type' })], []);

    expect(host.querySelector('.type')?.textContent?.trim()).toBe('retired-type');
  });

  it('says the shelf is empty rather than rendering nothing at all', () => {
    expect(render([]).querySelector('.empty')).not.toBeNull();
  });

  // ─── covers ─────────────────────────────────────────────────────────

  it('draws the library’s cover when the book has one', () => {
    const host = render([book('a', { coverUrl: 'https://host/api/library/cover/a.jpg' })]);

    expect(host.querySelector<HTMLImageElement>('img.cover')!.src)
      .toBe('https://host/api/library/cover/a.jpg');
  });

  /** Or the titles stop lining up, which is harder to read down than plain tiles. */
  it('draws a placeholder of the same size for a book with no cover', () => {
    const host = render([book('a')]);

    expect(host.querySelector('img.cover')).toBeNull();
    expect(host.querySelector('.cover.placeholder')).not.toBeNull();
  });

  /**
   * The library's index outlives the files it names. Left to the browser, a
   * cover that 404s is a broken-image glyph in the row.
   */
  it('falls back to the placeholder for a cover that will not load', () => {
    const host = render([book('a', { coverUrl: 'https://host/gone.jpg' })]);

    host.querySelector<HTMLImageElement>('img.cover')!.dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(host.querySelector('img.cover')).toBeNull();
    expect(host.querySelector('.cover.placeholder')).not.toBeNull();
  });

  /** One book's missing cover is not every book's. */
  it('keeps the other covers when one of them fails', () => {
    const host = render([
      book('a', { coverUrl: 'https://host/gone.jpg' }),
      book('b', { coverUrl: 'https://host/fine.jpg' })
    ]);

    host.querySelector<HTMLImageElement>('img.cover')!.dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(host.querySelectorAll('img.cover').length).toBe(1);
    expect(host.querySelectorAll('.cover.placeholder').length).toBe(1);
  });

  // ─── the way out ────────────────────────────────────────────────────

  /** Enrolling happens in the library, so the shelf always offers the way there. */
  it('offers the library whether or not there are books on the shelf', () => {
    expect(render([]).querySelector('.browse')).not.toBeNull();
    expect(render([book('a')]).querySelector('.browse')).not.toBeNull();
  });

  /** An anchor, so it middle-clicks and opens in a new tab like any other link. */
  it('links to the library rather than handling the click itself', () => {
    const link = render([book('a')]).querySelector<HTMLAnchorElement>('a.browse');

    expect(link).not.toBeNull();
    expect(link!.getAttribute('href')).toBe('/library');
  });
});
