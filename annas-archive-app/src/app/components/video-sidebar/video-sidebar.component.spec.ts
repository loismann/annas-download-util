import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { VideoSidebarComponent } from './video-sidebar.component';

/**
 * Characterization tests for the video library sidebar.
 *
 * Entirely a set of inputs and outputs — it owns no state, which is the point:
 * the grid holds the filters and this reports intent, so the two cannot drift
 * into disagreeing about what is currently filtered.
 */
describe('VideoSidebarComponent (characterization)', () => {
  let fixture: ComponentFixture<VideoSidebarComponent>;
  let component: VideoSidebarComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [VideoSidebarComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(VideoSidebarComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  it('should report every filter change rather than hold it', () => {
    const spies = {
      searchTermChange: jasmine.createSpy(),
      selectedGenreChange: jasmine.createSpy(),
      minPersonalRatingChange: jasmine.createSpy()
    };
    component.searchTermChange.subscribe(spies.searchTermChange);
    component.selectedGenreChange.subscribe(spies.selectedGenreChange);
    component.minPersonalRatingChange.subscribe(spies.minPersonalRatingChange);

    component.onSearchTermChange('apollo');
    component.onSelectedGenreChange('Space');
    component.onMinPersonalRatingChange(4);

    expect(spies.searchTermChange).toHaveBeenCalledWith('apollo');
    expect(spies.selectedGenreChange).toHaveBeenCalledWith('Space');
    expect(spies.minPersonalRatingChange).toHaveBeenCalledWith(4);
  });

  it('should report reset, bulk mode and select-all', () => {
    const spies = {
      resetView: jasmine.createSpy(),
      bulkEditToggle: jasmine.createSpy(),
      selectAllVisible: jasmine.createSpy()
    };
    component.resetView.subscribe(spies.resetView);
    component.bulkEditToggle.subscribe(spies.bulkEditToggle);
    component.selectAllVisible.subscribe(spies.selectAllVisible);

    component.onResetView();
    component.onBulkEditToggle();
    component.onSelectAllVisible();

    Object.values(spies).forEach(spy => expect(spy).toHaveBeenCalled());
  });

  it('should not change its own inputs when reporting', () => {
    // The grid owns the filters; a sidebar that also updated itself could show
    // a filter the grid never applied.
    component.searchTerm = 'before';

    component.onSearchTermChange('after');

    expect(component.searchTerm).toBe('before');
  });

  it('should start with nothing filtered and no admin tools', () => {
    expect(component.searchTerm).toBe('');
    expect(component.selectedGenre).toBe('');
    expect(component.minPersonalRating).toBe(0);
    expect(component.bulkEditMode).toBe(false);
    expect(component.isAdmin).toBe(false);
  });

  it('should always show the library total', () => {
    component.totalVideos = 500;
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('500');
  });

  it('should keep the bulk tools to admins', () => {
    component.bulkEditMode = true;
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Bulk Edit');

    component.isAdmin = true;
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Bulk Edit');
  });

  it('should say how many Select All Visible would take', () => {
    // The count belongs on the button rather than beside it: after filtering,
    // "select all" means the filtered set, and the number is what says so.
    component.isAdmin = true;
    component.bulkEditMode = true;
    component.visibleVideosCount = 12;
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Select All Visible (12)');
  });

  it('should report how many are selected once some are', () => {
    component.isAdmin = true;
    component.bulkEditMode = true;
    component.selectedVideosCount = 1;
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('1 video selected');

    component.selectedVideosCount = 3;
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('3 videos selected');
  });
});
