import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChapterListComponent } from './chapter-list.component';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { DropboxEpubChapter } from '../../models/dropbox-epub.model';

describe('ChapterListComponent', () => {
  let component: ChapterListComponent;
  let fixture: ComponentFixture<ChapterListComponent>;

  const mockChapters: DropboxEpubChapter[] = [
    { id: 1, title: 'Chapter 1', level: 0, wordCount: 1500, displayLabel: 'Introduction' },
    { id: 2, title: 'Chapter 2', level: 0, wordCount: 2500, displayLabel: null },
    { id: 3, title: 'Section 2.1', level: 1, wordCount: 800, displayLabel: 'Subsection' }
  ];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ChapterListComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(ChapterListComponent);
    component = fixture.componentInstance;
    component.chapters = [...mockChapters];
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  describe('Inputs', () => {
    it('should display chapters in dropdown', () => {
      const select = fixture.nativeElement.querySelector('mat-select');
      expect(select).toBeTruthy();
    });

    it('should be disabled when no chapters', () => {
      component.chapters = [];
      fixture.detectChanges();
      expect(component.isDisabled).toBe(true);
    });

    it('should be disabled when loadingChapters is true', () => {
      component.loadingChapters = true;
      fixture.detectChanges();
      expect(component.isDisabled).toBe(true);
    });

    it('should be disabled when loadingContent is true', () => {
      component.loadingContent = true;
      fixture.detectChanges();
      expect(component.isDisabled).toBe(true);
    });

    it('should be disabled when disabled input is true', () => {
      component.disabled = true;
      fixture.detectChanges();
      expect(component.isDisabled).toBe(true);
    });

    it('should not be disabled when chapters exist and not loading', () => {
      component.chapters = mockChapters;
      component.loadingChapters = false;
      component.loadingContent = false;
      component.disabled = false;
      fixture.detectChanges();
      expect(component.isDisabled).toBe(false);
    });

    it('should use selectedChapterId as value', () => {
      component.selectedChapterId = 2;
      fixture.detectChanges();
      const select = fixture.nativeElement.querySelector('mat-select');
      expect(select.getAttribute('ng-reflect-value')).toBe('2');
    });
  });

  describe('Outputs', () => {
    it('should emit chapterSelected when selection changes', () => {
      spyOn(component.chapterSelected, 'emit');
      component.onSelectionChange(2);
      expect(component.chapterSelected.emit).toHaveBeenCalledWith(2);
    });
  });

  describe('Cached chapters', () => {
    it('should return true for cached chapter', () => {
      component.cachedChapterIds = new Set([1, 3]);
      expect(component.isCached(1)).toBe(true);
      expect(component.isCached(3)).toBe(true);
    });

    it('should return false for non-cached chapter', () => {
      component.cachedChapterIds = new Set([1]);
      expect(component.isCached(2)).toBe(false);
    });

    it('should return false when no chapters are cached', () => {
      component.cachedChapterIds = new Set();
      expect(component.isCached(1)).toBe(false);
    });
  });

  describe('Display', () => {
    it('should apply dropdown-disabled class when disabled', () => {
      component.chapters = [];
      fixture.detectChanges();
      const formField = fixture.nativeElement.querySelector('mat-form-field');
      expect(formField.classList.contains('dropdown-disabled')).toBe(true);
    });

    it('should not apply dropdown-disabled class when enabled', () => {
      component.chapters = mockChapters;
      component.loadingChapters = false;
      component.loadingContent = false;
      fixture.detectChanges();
      const formField = fixture.nativeElement.querySelector('mat-form-field');
      expect(formField.classList.contains('dropdown-disabled')).toBe(false);
    });
  });
});
