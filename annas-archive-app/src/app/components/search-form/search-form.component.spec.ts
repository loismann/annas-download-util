import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { SearchFormComponent, DomainHealth, SearchFormSubmitEvent } from './search-form.component';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { AiApiService } from '../../services/ai-api.service';
import { LoggerService } from '../../services/logger.service';
import { of, throwError } from 'rxjs';

describe('SearchFormComponent', () => {
  let component: SearchFormComponent;
  let fixture: ComponentFixture<SearchFormComponent>;
  let mockAiApiService: jasmine.SpyObj<AiApiService>;
  let mockLoggerService: jasmine.SpyObj<LoggerService>;

  const mockDomains: DomainHealth[] = [
    { name: "Anna's Archive ORG", extension: 'org', health: 95, certExpDays: 30 },
    { name: "Anna's Archive SE", extension: 'se', health: 72, certExpDays: 15 },
    { name: "Anna's Archive LI", extension: 'li', health: 45, certExpDays: 60 }
  ];

  beforeEach(async () => {
    mockAiApiService = jasmine.createSpyObj('AiApiService', ['suggestAuthors']);
    mockLoggerService = jasmine.createSpyObj('LoggerService', ['log', 'error']);

    mockAiApiService.suggestAuthors.and.returnValue(of({ authors: [] }));

    await TestBed.configureTestingModule({
      imports: [SearchFormComponent, NoopAnimationsModule],
      providers: [
        { provide: AiApiService, useValue: mockAiApiService },
        { provide: LoggerService, useValue: mockLoggerService }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SearchFormComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  describe('Inputs', () => {
    it('should display loading state', () => {
      component.loading = true;
      fixture.detectChanges();
      const loadingEl = fixture.nativeElement.querySelector('.loading');
      expect(loadingEl).toBeTruthy();
    });

    it('should display error message', () => {
      component.error = 'Test error message';
      fixture.detectChanges();
      const errorEl = fixture.nativeElement.querySelector('.error');
      expect(errorEl.textContent).toContain('Test error message');
    });

    it('should display domain health items', () => {
      component.annaDomains = mockDomains;
      fixture.detectChanges();
      const healthItems = fixture.nativeElement.querySelectorAll('.health-item');
      expect(healthItems.length).toBe(3);
    });

    it('should apply collapsed class when collapsed', () => {
      component.collapsed = true;
      fixture.detectChanges();
      const form = fixture.nativeElement.querySelector('.search-form');
      expect(form.classList.contains('collapsed')).toBe(true);
    });
  });

  describe('Health color classes', () => {
    it('should return health-green for health >= 90', () => {
      expect(component.getHealthColorClass(95)).toBe('health-green');
      expect(component.getHealthColorClass(90)).toBe('health-green');
    });

    it('should return health-yellow for health 70-89', () => {
      expect(component.getHealthColorClass(89)).toBe('health-yellow');
      expect(component.getHealthColorClass(70)).toBe('health-yellow');
    });

    it('should return health-orange for health 50-69', () => {
      expect(component.getHealthColorClass(69)).toBe('health-orange');
      expect(component.getHealthColorClass(50)).toBe('health-orange');
    });

    it('should return health-red for health < 50', () => {
      expect(component.getHealthColorClass(49)).toBe('health-red');
      expect(component.getHealthColorClass(0)).toBe('health-red');
    });

    it('should return health-unknown for null health', () => {
      expect(component.getHealthColorClass(null)).toBe('health-unknown');
    });
  });

  describe('Outputs', () => {
    it('should emit search event on form submit', () => {
      spyOn(component.search, 'emit');
      component.searchTerm = 'Test Book';
      component.selectedAuthor = 'Test Author';
      component.selectedFormat = 'EPUB';
      component.useLibGen = false;

      component.onSubmit();

      expect(component.search.emit).toHaveBeenCalledWith({
        searchTerm: 'Test Book',
        selectedAuthor: 'Test Author',
        selectedFormat: 'EPUB',
        useLibGen: false,
        isAiSearch: false
      } as SearchFormSubmitEvent);
    });

    it('should emit search event with AI search data when expanded', () => {
      spyOn(component.search, 'emit');
      component.aiSearchExpanded = true;
      component.aiSearchQuery = 'Find me sci-fi books';
      component.searchTerm = 'Test';
      component.selectedFormat = 'PDF';

      component.onSubmit();

      expect(component.search.emit).toHaveBeenCalledWith(jasmine.objectContaining({
        isAiSearch: true,
        aiSearchQuery: 'Find me sci-fi books'
      }));
    });

    it('should emit openRelatedBooks event', () => {
      spyOn(component.openRelatedBooks, 'emit');
      component.searchTerm = 'Game of Thrones';
      component.selectedAuthor = 'George R.R. Martin';

      component.onOpenRelatedBooks();

      expect(component.openRelatedBooks.emit).toHaveBeenCalledWith({
        searchTerm: 'Game of Thrones',
        author: 'George R.R. Martin'
      });
    });

    it('should not emit openRelatedBooks if search term empty', () => {
      spyOn(component.openRelatedBooks, 'emit');
      component.searchTerm = '';
      component.selectedAuthor = 'George R.R. Martin';

      component.onOpenRelatedBooks();

      expect(component.openRelatedBooks.emit).not.toHaveBeenCalled();
    });

    it('should not emit openRelatedBooks if author not selected', () => {
      spyOn(component.openRelatedBooks, 'emit');
      component.searchTerm = 'Game of Thrones';
      component.selectedAuthor = '';

      component.onOpenRelatedBooks();

      expect(component.openRelatedBooks.emit).not.toHaveBeenCalled();
    });
  });

  describe('AI Search toggle', () => {
    it('should toggle aiSearchExpanded state', () => {
      expect(component.aiSearchExpanded).toBe(false);
      component.toggleAiSearch();
      expect(component.aiSearchExpanded).toBe(true);
      component.toggleAiSearch();
      expect(component.aiSearchExpanded).toBe(false);
    });

    it('should copy searchTerm to aiSearchQuery when expanding', () => {
      component.searchTerm = 'Test query';
      component.toggleAiSearch();
      expect(component.aiSearchQuery).toBe('Test query');
    });

    it('should clear aiSearchQuery when collapsing', () => {
      component.aiSearchExpanded = true;
      component.aiSearchQuery = 'Some query';
      component.toggleAiSearch();
      expect(component.aiSearchQuery).toBe('');
    });
  });

  describe('Format change', () => {
    it('should update selectedFormat', () => {
      component.onFormatChange('MOBI');
      expect(component.selectedFormat).toBe('MOBI');
    });
  });

  describe('Author suggestions', () => {
    it('should fetch author suggestions on search term change', fakeAsync(() => {
      mockAiApiService.suggestAuthors.and.returnValue(of({
        authors: [{ author: 'Brandon Sanderson', confidence: 'high' }]
      }));

      component.onSearchTermChange('Mistborn');
      tick(600); // Wait for debounce

      expect(mockAiApiService.suggestAuthors).toHaveBeenCalledWith('Mistborn');
      expect(component.authorSuggestions.length).toBe(1);
    }));

    it('should clear suggestions for short search terms', () => {
      component.authorSuggestions = [{ author: 'Test', confidence: 'low' }];
      component.onSearchTermChange('ab');
      expect(component.authorSuggestions.length).toBe(0);
    });

    it('should not fetch suggestions when AI search expanded', fakeAsync(() => {
      component.aiSearchExpanded = true;
      component.onSearchTermChange('Mistborn');
      tick(600);
      expect(mockAiApiService.suggestAuthors).not.toHaveBeenCalled();
    }));

    it('should handle author suggestion errors gracefully', fakeAsync(() => {
      mockAiApiService.suggestAuthors.and.returnValue(throwError(() => new Error('API Error')));

      component.onSearchTermChange('Test Book Title');
      tick(600);

      expect(component.authorSuggestions).toEqual([]);
      expect(component.loadingAuthors).toBe(false);
      expect(mockLoggerService.error).toHaveBeenCalled();
    }));
  });

  describe('Available formats', () => {
    it('should have static list of formats', () => {
      expect(component.availableFormats).toContain('EPUB');
      expect(component.availableFormats).toContain('MOBI');
      expect(component.availableFormats).toContain('PDF');
      expect(component.availableFormats).toContain('AZW3');
    });
  });
});
