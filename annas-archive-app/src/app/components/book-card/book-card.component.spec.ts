import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BookCardComponent, LibraryBook } from './book-card.component';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

describe('BookCardComponent', () => {
  let component: BookCardComponent;
  let fixture: ComponentFixture<BookCardComponent>;

  const mockBook: LibraryBook = {
    title: 'Test Book Title',
    authors: ['Test Author'],
    format: 'EPUB',
    fileSize: '1.5 MB',
    fileName: 'test-book.epub',
    coverUrl: 'https://example.com/cover.jpg',
    goodreadsRating: 4.5,
    personalRating: 3,
    dadsKindleState: 'idle',
    momsKindleState: 'idle'
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BookCardComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(BookCardComponent);
    component = fixture.componentInstance;
    component.book = { ...mockBook };
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  describe('Inputs', () => {
    it('should display book title', () => {
      const titleEl = fixture.nativeElement.querySelector('.title');
      expect(titleEl.textContent).toContain('Test Book Title');
    });

    it('should display book authors', () => {
      const authorEl = fixture.nativeElement.querySelector('.author');
      expect(authorEl.textContent).toContain('Test Author');
    });

    it('should display format and file size', () => {
      const metaEl = fixture.nativeElement.querySelector('.meta');
      expect(metaEl.textContent).toContain('EPUB');
      expect(metaEl.textContent).toContain('1.5 MB');
    });

    it('should display goodreads rating', () => {
      const goodreadsEl = fixture.nativeElement.querySelector('.goodreads-value');
      expect(goodreadsEl.textContent.trim()).toBe('4.5');
    });

    it('should display NA for missing goodreads rating', () => {
      component.book = { ...mockBook, goodreadsRating: null };
      fixture.detectChanges();
      const goodreadsEl = fixture.nativeElement.querySelector('.goodreads-value');
      expect(goodreadsEl.textContent.trim()).toBe('NA');
    });

    it('should apply small tile size class', () => {
      component.tileSize = 'small';
      fixture.detectChanges();
      const card = fixture.nativeElement.querySelector('.library-card');
      expect(card.classList.contains('library-card-small')).toBe(true);
    });

    it('should apply large tile size class', () => {
      component.tileSize = 'large';
      fixture.detectChanges();
      const card = fixture.nativeElement.querySelector('.library-card');
      expect(card.classList.contains('library-card-large')).toBe(true);
    });

    it('should show bulk edit checkbox when bulkEditMode is true', () => {
      component.bulkEditMode = true;
      fixture.detectChanges();
      const checkbox = fixture.nativeElement.querySelector('.bulk-edit-checkbox-wrapper');
      expect(checkbox).toBeTruthy();
    });

    it('should hide bulk edit checkbox when bulkEditMode is false', () => {
      component.bulkEditMode = false;
      fixture.detectChanges();
      const checkbox = fixture.nativeElement.querySelector('.bulk-edit-checkbox-wrapper');
      expect(checkbox).toBeFalsy();
    });

    it('should apply bulk-edit-selected class when selected in bulk mode', () => {
      component.bulkEditMode = true;
      component.isSelected = true;
      fixture.detectChanges();
      const card = fixture.nativeElement.querySelector('.library-card');
      expect(card.classList.contains('bulk-edit-selected')).toBe(true);
    });
  });

  describe('Outputs', () => {
    it('should emit coverClick when cover is clicked', () => {
      spyOn(component.coverClick, 'emit');
      const coverFrame = fixture.nativeElement.querySelector('.cover-frame');
      coverFrame.click();
      expect(component.coverClick.emit).toHaveBeenCalledWith(component.book);
    });

    it('should emit ratingChange when star is clicked', () => {
      spyOn(component.ratingChange, 'emit');
      const starButtons = fixture.nativeElement.querySelectorAll('.star-button');
      starButtons[2].click(); // Click 3rd star
      expect(component.ratingChange.emit).toHaveBeenCalledWith({
        book: component.book,
        rating: 3
      });
    });

    it('should emit sendToKindle for dad when dad button clicked', () => {
      component.canSendToKindle = true;
      fixture.detectChanges();
      spyOn(component.sendToKindle, 'emit');
      const buttons = fixture.nativeElement.querySelectorAll('.card-actions button');
      buttons[0].click();
      expect(component.sendToKindle.emit).toHaveBeenCalledWith({
        book: component.book,
        target: 'dad'
      });
    });

    it('should emit sendToKindle for mom when mom button clicked', () => {
      component.canSendToKindle = true;
      fixture.detectChanges();
      spyOn(component.sendToKindle, 'emit');
      const buttons = fixture.nativeElement.querySelectorAll('.card-actions button');
      buttons[1].click();
      expect(component.sendToKindle.emit).toHaveBeenCalledWith({
        book: component.book,
        target: 'mom'
      });
    });

    it('should emit selectionToggle when checkbox is clicked', () => {
      component.bulkEditMode = true;
      fixture.detectChanges();
      spyOn(component.selectionToggle, 'emit');
      const checkbox = fixture.nativeElement.querySelector('mat-checkbox');
      checkbox.querySelector('input').click();
      expect(component.selectionToggle.emit).toHaveBeenCalledWith(component.book);
    });
  });

  describe('Star rating display', () => {
    it('should fill correct number of stars based on personalRating', () => {
      component.book = { ...mockBook, personalRating: 3 };
      fixture.detectChanges();
      const filledStars = fixture.nativeElement.querySelectorAll('.star-button.filled');
      expect(filledStars.length).toBe(3);
    });

    it('should show no filled stars when personalRating is 0', () => {
      component.book = { ...mockBook, personalRating: 0 };
      fixture.detectChanges();
      const filledStars = fixture.nativeElement.querySelectorAll('.star-button.filled');
      expect(filledStars.length).toBe(0);
    });

    it('should show all filled stars when personalRating is 5', () => {
      component.book = { ...mockBook, personalRating: 5 };
      fixture.detectChanges();
      const filledStars = fixture.nativeElement.querySelectorAll('.star-button.filled');
      expect(filledStars.length).toBe(5);
    });
  });

  describe('Kindle button states', () => {
    it('should disable kindle button when canSendToKindle is false', () => {
      component.canSendToKindle = false;
      fixture.detectChanges();
      const buttons = fixture.nativeElement.querySelectorAll('.card-actions button');
      expect(buttons[0].disabled).toBe(true);
      expect(buttons[1].disabled).toBe(true);
    });

    it('should show Sending state when dadsKindleState is sending', () => {
      component.book = { ...mockBook, dadsKindleState: 'sending' };
      component.canSendToKindle = true;
      fixture.detectChanges();
      const buttons = fixture.nativeElement.querySelectorAll('.card-actions button');
      expect(buttons[0].textContent).toContain('Sending');
    });

    it('should show Sent state when dadsKindleState is success', () => {
      component.book = { ...mockBook, dadsKindleState: 'success' };
      component.canSendToKindle = true;
      fixture.detectChanges();
      const buttons = fixture.nativeElement.querySelectorAll('.card-actions button');
      expect(buttons[0].textContent).toContain('Sent');
    });

    it('should show Retry state when dadsKindleState is error', () => {
      component.book = { ...mockBook, dadsKindleState: 'error' };
      component.canSendToKindle = true;
      fixture.detectChanges();
      const buttons = fixture.nativeElement.querySelectorAll('.card-actions button');
      expect(buttons[0].textContent).toContain('Retry');
    });
  });

  describe('Cover error handling', () => {
    it('should set placeholder on cover error', () => {
      const img = fixture.nativeElement.querySelector('.cover') as HTMLImageElement;
      const errorEvent = new Event('error');
      Object.defineProperty(errorEvent, 'target', { value: img });

      component.onCoverError(errorEvent);

      expect(img.src).toContain(component.placeholderUrl);
    });

    it('should emit coverError event on cover error', () => {
      spyOn(component.coverError, 'emit');
      const img = fixture.nativeElement.querySelector('.cover') as HTMLImageElement;
      const errorEvent = new Event('error');
      Object.defineProperty(errorEvent, 'target', { value: img });

      component.onCoverError(errorEvent);

      expect(component.coverError.emit).toHaveBeenCalledWith(errorEvent);
    });

    it('should not set placeholder if already using placeholder', () => {
      const img = fixture.nativeElement.querySelector('.cover') as HTMLImageElement;
      img.src = component.placeholderUrl;
      const originalSrc = img.src;
      const errorEvent = new Event('error');
      Object.defineProperty(errorEvent, 'target', { value: img });

      component.onCoverError(errorEvent);

      expect(img.src).toBe(originalSrc);
    });
  });

  describe('Author display', () => {
    it('should display multiple authors joined by comma', () => {
      component.book = { ...mockBook, authors: ['Author One', 'Author Two'] };
      fixture.detectChanges();
      const authorEl = fixture.nativeElement.querySelector('.author');
      expect(authorEl.textContent).toContain('Author One, Author Two');
    });

    it('should apply author-missing class when no authors', () => {
      component.book = { ...mockBook, authors: [] };
      fixture.detectChanges();
      const authorEl = fixture.nativeElement.querySelector('.author');
      expect(authorEl.classList.contains('author-missing')).toBe(true);
    });
  });

  describe('Bookmark functionality', () => {
    it('should display bookmark button', () => {
      const bookmarkBtn = fixture.nativeElement.querySelector('.bookmark-btn');
      expect(bookmarkBtn).toBeTruthy();
    });

    it('should show empty bookmark icon when not bookmarked', () => {
      component.book = { ...mockBook, bookmarked: false };
      fixture.detectChanges();
      const bookmarkBtn = fixture.nativeElement.querySelector('.bookmark-btn');
      expect(bookmarkBtn.textContent).toContain('bookmark_border');
      expect(bookmarkBtn.classList.contains('bookmarked')).toBe(false);
    });

    it('should show filled bookmark icon when bookmarked', () => {
      component.book = { ...mockBook, bookmarked: true };
      fixture.detectChanges();
      const bookmarkBtn = fixture.nativeElement.querySelector('.bookmark-btn');
      expect(bookmarkBtn.textContent).toContain('bookmark');
      expect(bookmarkBtn.classList.contains('bookmarked')).toBe(true);
    });

    it('should emit bookmarkToggle when bookmark button is clicked', () => {
      spyOn(component.bookmarkToggle, 'emit');
      const bookmarkBtn = fixture.nativeElement.querySelector('.bookmark-btn');
      bookmarkBtn.click();
      expect(component.bookmarkToggle.emit).toHaveBeenCalledWith(component.book);
    });
  });
});
