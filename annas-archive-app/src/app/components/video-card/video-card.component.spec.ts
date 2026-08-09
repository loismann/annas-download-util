import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { VideoCardComponent } from './video-card.component';
import { VideoDto } from '../../services/video-library-api.service';

/**
 * Characterization tests for the video grid card.
 *
 * Presentational: it reports what was clicked and leaves every decision to the
 * grid. The meta line and the thumbnail fallback are the only two pieces of
 * logic, and both are about a video whose metadata came back incomplete.
 */
describe('VideoCardComponent (characterization)', () => {
  let fixture: ComponentFixture<VideoCardComponent>;
  let component: VideoCardComponent;

  function video(over: Partial<VideoDto> = {}): VideoDto {
    return {
      fileName: 'a.mp4', title: 'A Video', channel: 'A Channel', duration: '10:00',
      durationSeconds: 600, format: 'mp4', resolution: '1080p', fileSize: '100 MB',
      thumbnailUrl: null, description: null, primaryGenre: null, tags: [], playlist: null,
      youTubeId: null, personalRating: null, bookmarked: null,
      downloadedAt: null, publishedAt: null, ...over
    };
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [VideoCardComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(VideoCardComponent);
    component = fixture.componentInstance;
    component.video = video();
  });

  afterEach(() => fixture.destroy());

  describe('the meta line', () => {
    it('should join what is known', () => {
      expect(component.metaLine).toBe('1080p | mp4 | 100 MB');
    });

    it('should leave out what is missing rather than show gaps', () => {
      // A separator with nothing either side reads as broken metadata.
      component.video = video({ resolution: null, fileSize: '' });

      expect(component.metaLine).toBe('mp4');
    });

    it('should be empty when nothing is known', () => {
      component.video = video({ resolution: null, format: '', fileSize: '' });

      expect(component.metaLine).toBe('');
    });
  });

  describe('the thumbnail', () => {
    it('should swap a broken thumbnail for the placeholder and say so', () => {
      const img = document.createElement('img');
      img.src = 'http://x/broken.jpg';
      const reported = jasmine.createSpy('thumbnailError');
      component.thumbnailError.subscribe(reported);

      component.onThumbnailError({ target: img } as unknown as Event);

      expect(img.src).toContain('/assets/video-placeholder.jpg');
      expect(reported).toHaveBeenCalled();
    });

    it('should not loop when the placeholder itself failed', () => {
      const img = document.createElement('img');
      img.src = `${location.origin}/assets/video-placeholder.jpg`;
      const reported = jasmine.createSpy('thumbnailError');
      component.thumbnailError.subscribe(reported);

      component.onThumbnailError({ target: img } as unknown as Event);

      expect(reported).not.toHaveBeenCalled();
    });
  });

  describe('reporting clicks', () => {
    it('should report each action with the video it belongs to', () => {
      const spies = {
        thumbnailClick: jasmine.createSpy(),
        bookmarkToggle: jasmine.createSpy(),
        editClick: jasmine.createSpy(),
        playClick: jasmine.createSpy(),
        selectionToggle: jasmine.createSpy()
      };
      component.thumbnailClick.subscribe(spies.thumbnailClick);
      component.bookmarkToggle.subscribe(spies.bookmarkToggle);
      component.editClick.subscribe(spies.editClick);
      component.playClick.subscribe(spies.playClick);
      component.selectionToggle.subscribe(spies.selectionToggle);

      component.onThumbnailClick();
      component.onBookmarkToggle();
      component.onEditClick();
      component.onPlayClick();
      component.onSelectionToggle();

      Object.values(spies).forEach(spy => expect(spy).toHaveBeenCalledWith(component.video));
    });

    it('should report a rating with the video it applies to', () => {
      const rated = jasmine.createSpy('ratingChange');
      component.ratingChange.subscribe(rated);

      component.setPersonalRating(4);

      expect(rated).toHaveBeenCalledWith({ video: component.video, rating: 4 });
    });

    it('should offer five stars', () => {
      expect(component.starRange).toEqual([1, 2, 3, 4, 5]);
    });
  });

  describe('defaults', () => {
    it('should start medium, unselected and out of bulk mode', () => {
      expect(component.tileSize).toBe('medium');
      expect(component.bulkEditMode).toBe(false);
      expect(component.isSelected).toBe(false);
    });
  });
});
