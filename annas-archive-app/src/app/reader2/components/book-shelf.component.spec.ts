import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BookShelfComponent } from './book-shelf.component';
import { Book, Lens } from '../reader2.models';

function book(id: string, extra: Partial<Book> = {}): Book {
  return {
    bookId: id, fileName: `${id}.epub`, title: `Book ${id}`, authors: ['An Author'],
    lensKey: 'ideas-key', addedAtUtc: '', lastOpenedAtUtc: null, isAvailable: true, ...extra
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
    await TestBed.configureTestingModule({ imports: [BookShelfComponent] }).compileComponents();

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
});
