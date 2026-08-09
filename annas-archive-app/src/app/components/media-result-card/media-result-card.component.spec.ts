import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { MediaResultCardComponent } from './media-result-card.component';
import { MediaLookupResult } from '../../services/media-search-api.service';

/**
 * Characterization tests for the search result card.
 *
 * The season summary is the part with real logic, and its whole job is to keep
 * two different states apart: a season that has been *asked for* and one that
 * actually has files. Conflating them would tell the user a show is ready to
 * watch while Sonarr is still looking for it.
 */
describe('MediaResultCardComponent (characterization)', () => {
  let fixture: ComponentFixture<MediaResultCardComponent>;
  let component: MediaResultCardComponent;

  function result(over: Partial<MediaLookupResult> = {}): MediaLookupResult {
    return { title: 'The Outer Limits', ...over };
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MediaResultCardComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(MediaResultCardComponent);
    component = fixture.componentInstance;
    component.result = result();
  });

  afterEach(() => fixture.destroy());

  describe('the season summary', () => {
    it('should say nothing for a show that is not in the library', () => {
      expect(component.alreadyAddedLabel).toBeNull();
    });

    it('should keep requested seasons apart from downloaded ones', () => {
      component.alreadyAddedSeasons = [1, 2, 3];
      component.downloadedSeasons = [1, 2];

      expect(component.alreadyAddedLabel).toBe('Downloaded: S1, S2 · Requested: S3');
    });

    it('should show only what applies', () => {
      component.alreadyAddedSeasons = [1];
      component.downloadedSeasons = [1];
      expect(component.alreadyAddedLabel).toBe('Downloaded: S1');

      component.downloadedSeasons = [];
      expect(component.alreadyAddedLabel).toBe('Requested: S1');
    });

    it('should call season zero Specials', () => {
      component.alreadyAddedSeasons = [0, 1];
      component.downloadedSeasons = [0];

      expect(component.alreadyAddedLabel).toBe('Downloaded: Specials · Requested: S1');
    });

    it('should list seasons in order however they arrived', () => {
      component.alreadyAddedSeasons = [3, 1, 2];
      component.downloadedSeasons = [3, 1, 2];

      expect(component.alreadyAddedLabel).toBe('Downloaded: S1, S2, S3');
    });

    it('should treat a missing downloaded list as none downloaded', () => {
      component.alreadyAddedSeasons = [1, 2];

      expect(component.alreadyAddedLabel).toBe('Requested: S1, S2');
    });
  });

  describe('the button', () => {
    it('should offer to add a show that is not there', () => {
      expect(component.buttonLabel).toBe('Add');
    });

    it('should offer to manage seasons for one that is', () => {
      // Adding again would ask Sonarr to create a series it already has.
      component.alreadyAddedSeasons = [1];

      expect(component.buttonLabel).toBe('Manage Seasons');
    });

    it('should report the result it was given', () => {
      const clicked = jasmine.createSpy('add');
      component.add.subscribe(clicked);

      component.onAddClick();

      expect(clicked).toHaveBeenCalledWith(component.result);
    });

    it('should start idle', () => {
      expect(component.addState).toBe('idle');
      expect(component.progressLabel).toBeNull();
    });
  });

  describe('the poster', () => {
    it('should prefer the remote URL over the local one', () => {
      // Sonarr/Radarr's own cached copy may not exist yet for a search result.
      component.result = result({
        images: [{ coverType: 'poster', url: '/local.jpg', remoteUrl: 'http://x/remote.jpg' }]
      });

      expect(component.posterUrl).toBe('http://x/remote.jpg');
    });

    it('should fall back to the local URL', () => {
      component.result = result({ images: [{ coverType: 'poster', url: '/local.jpg' }] });

      expect(component.posterUrl).toBe('/local.jpg');
    });

    it('should ignore images that are not posters', () => {
      component.result = result({ images: [{ coverType: 'fanart', url: '/wide.jpg' }] });

      expect(component.posterUrl).toBe('/assets/placeholder.jpg');
    });

    it('should fall back to the placeholder with no images at all', () => {
      expect(component.posterUrl).toBe('/assets/placeholder.jpg');
    });

    it('should swap a broken poster for the placeholder, once', () => {
      const img = document.createElement('img');
      img.src = 'http://x/broken.jpg';

      component.onImgError({ target: img } as unknown as Event);
      expect(img.src).toContain('/assets/placeholder.jpg');

      const before = img.src;
      component.onImgError({ target: img } as unknown as Event);
      expect(img.src).toBe(before);
    });
  });
});
