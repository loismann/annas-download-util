import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LibrarySidebarComponent } from './library-sidebar.component';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

describe('LibrarySidebarComponent', () => {
  let component: LibrarySidebarComponent;
  let fixture: ComponentFixture<LibrarySidebarComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LibrarySidebarComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(LibrarySidebarComponent);
    component = fixture.componentInstance;
    component.genres = ['Fiction', 'Non-Fiction', 'Sci-Fi'];
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  describe('Inputs', () => {
    it('should display sidebar header', () => {
      const header = fixture.nativeElement.querySelector('.sidebar-header h2');
      expect(header.textContent).toContain('Ebook Library');
    });

    it('should display genres in dropdown', () => {
      const select = fixture.nativeElement.querySelector('mat-select');
      expect(select).toBeTruthy();
    });

    it('should display total books count', () => {
      component.totalBooks = 42;
      fixture.detectChanges();
      const meta = fixture.nativeElement.querySelector('.meta-value');
      expect(meta.textContent).toContain('42');
    });

    it('should display bulk edit button', () => {
      const button = fixture.nativeElement.querySelector('.bulk-edit-toggle');
      expect(button.textContent).toContain('Bulk Edit');
    });

    it('should show Exit Bulk Edit when bulkEditMode is true', () => {
      component.bulkEditMode = true;
      fixture.detectChanges();
      const button = fixture.nativeElement.querySelector('.bulk-edit-toggle');
      expect(button.textContent).toContain('Exit Bulk Edit');
    });

    it('should show bulk edit controls when mode enabled and books selected', () => {
      component.bulkEditMode = true;
      component.selectedBooksCount = 3;
      fixture.detectChanges();
      const controls = fixture.nativeElement.querySelector('.bulk-edit-controls');
      expect(controls).toBeTruthy();
    });

    it('should hide bulk edit controls when no books selected', () => {
      component.bulkEditMode = true;
      component.selectedBooksCount = 0;
      fixture.detectChanges();
      const controls = fixture.nativeElement.querySelector('.bulk-edit-controls');
      expect(controls).toBeFalsy();
    });

    it('should show admin section when isAdmin is true', () => {
      component.isAdmin = true;
      fixture.detectChanges();
      const admin = fixture.nativeElement.querySelector('.admin-section');
      expect(admin).toBeTruthy();
    });

    it('should hide admin section when isAdmin is false', () => {
      component.isAdmin = false;
      fixture.detectChanges();
      const admin = fixture.nativeElement.querySelector('.admin-section');
      expect(admin).toBeFalsy();
    });

    it('should show admin panel when adminOpen is true', () => {
      component.isAdmin = true;
      component.adminOpen = true;
      fixture.detectChanges();
      const panel = fixture.nativeElement.querySelector('.admin-panel');
      expect(panel).toBeTruthy();
    });
  });

  describe('Outputs', () => {
    it('should emit resetView when Reset View button clicked', () => {
      spyOn(component.resetView, 'emit');
      const button = fixture.nativeElement.querySelector('.reset-view-btn');
      button.click();
      expect(component.resetView.emit).toHaveBeenCalled();
    });

    it('should emit searchTermChange when search input changes', () => {
      spyOn(component.searchTermChange, 'emit');
      component.onSearchTermChange('test query');
      expect(component.searchTermChange.emit).toHaveBeenCalledWith('test query');
    });

    it('should emit selectedGenreChange when genre changes', () => {
      spyOn(component.selectedGenreChange, 'emit');
      component.onSelectedGenreChange('Fiction');
      expect(component.selectedGenreChange.emit).toHaveBeenCalledWith('Fiction');
    });

    it('should emit minPersonalRatingChange when rating slider changes', () => {
      spyOn(component.minPersonalRatingChange, 'emit');
      component.onMinPersonalRatingChange(3);
      expect(component.minPersonalRatingChange.emit).toHaveBeenCalledWith(3);
    });

    it('should emit minGoodreadsRatingChange when rating slider changes', () => {
      spyOn(component.minGoodreadsRatingChange, 'emit');
      component.onMinGoodreadsRatingChange(4.5);
      expect(component.minGoodreadsRatingChange.emit).toHaveBeenCalledWith(4.5);
    });

    it('should emit bulkEditToggle when bulk edit button clicked', () => {
      spyOn(component.bulkEditToggle, 'emit');
      component.onBulkEditToggle();
      expect(component.bulkEditToggle.emit).toHaveBeenCalled();
    });

    it('should emit openBulkEdit when Edit Selected button clicked', () => {
      spyOn(component.openBulkEdit, 'emit');
      component.onOpenBulkEdit();
      expect(component.openBulkEdit.emit).toHaveBeenCalled();
    });

    it('should emit bulkSend with dropbox when Send to Dropbox clicked', () => {
      spyOn(component.bulkSend, 'emit');
      component.onBulkSend('dropbox');
      expect(component.bulkSend.emit).toHaveBeenCalledWith('dropbox');
    });

    it('should emit bulkSend with kindle-dad when Dad Kindle clicked', () => {
      spyOn(component.bulkSend, 'emit');
      component.onBulkSend('kindle-dad');
      expect(component.bulkSend.emit).toHaveBeenCalledWith('kindle-dad');
    });

    it('should emit bulkSend with kindle-mom when Mom Kindle clicked', () => {
      spyOn(component.bulkSend, 'emit');
      component.onBulkSend('kindle-mom');
      expect(component.bulkSend.emit).toHaveBeenCalledWith('kindle-mom');
    });

    it('should emit adminToggle when admin button clicked', () => {
      spyOn(component.adminToggle, 'emit');
      component.onAdminToggle();
      expect(component.adminToggle.emit).toHaveBeenCalled();
    });

    it('should emit filterMissingAuthorChange', () => {
      spyOn(component.filterMissingAuthorChange, 'emit');
      component.onFilterMissingAuthorChange(true);
      expect(component.filterMissingAuthorChange.emit).toHaveBeenCalledWith(true);
    });

    it('should emit filterMissingCoverChange', () => {
      spyOn(component.filterMissingCoverChange, 'emit');
      component.onFilterMissingCoverChange(true);
      expect(component.filterMissingCoverChange.emit).toHaveBeenCalledWith(true);
    });

    it('should emit wipeGenres when wipe button clicked', () => {
      spyOn(component.wipeGenres, 'emit');
      component.onWipeGenres();
      expect(component.wipeGenres.emit).toHaveBeenCalled();
    });
  });

  describe('Selection counter display', () => {
    it('should show singular "book" for 1 selection', () => {
      component.bulkEditMode = true;
      component.selectedBooksCount = 1;
      fixture.detectChanges();
      const counter = fixture.nativeElement.querySelector('.selection-counter');
      expect(counter.textContent).toContain('1 book selected');
    });

    it('should show plural "books" for multiple selections', () => {
      component.bulkEditMode = true;
      component.selectedBooksCount = 5;
      fixture.detectChanges();
      const counter = fixture.nativeElement.querySelector('.selection-counter');
      expect(counter.textContent).toContain('5 books selected');
    });
  });
});
