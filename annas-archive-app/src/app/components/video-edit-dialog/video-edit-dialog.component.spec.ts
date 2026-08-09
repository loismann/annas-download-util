import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Observable, of, throwError } from 'rxjs';

import {
  VideoEditDialogComponent, VideoEditDialogData
} from './video-edit-dialog.component';
import { VideoLibraryApiService } from '../../services/video-library-api.service';
import { LoggerService } from '../../services/logger.service';

/**
 * Characterization tests for the video edit dialog.
 *
 * A review pass in the same series as the library pages. It found the delete
 * routed through the component's read guard — see "deleting".
 *
 * The keyboard handling gets its own section because these are
 * `document:keydown` listeners: they fire wherever the focus is, so what they
 * decline to act on matters as much as what they do.
 */
describe('VideoEditDialogComponent (characterization)', () => {
  let fixture: ComponentFixture<VideoEditDialogComponent>;
  let component: VideoEditDialogComponent;
  let dialogRef: jasmine.SpyObj<MatDialogRef<VideoEditDialogComponent>>;
  let api: jasmine.SpyObj<VideoLibraryApiService>;

  function data(over: Partial<VideoEditDialogData> = {}): VideoEditDialogData {
    return {
      fileName: 'a.mp4', title: 'A Video', channel: 'A Channel', duration: '10:00',
      resolution: '1080p', format: 'mp4', fileSize: '100 MB', thumbnailUrl: null,
      description: null, primaryGenre: null, tags: [], playlist: null,
      youTubeId: null, availableGenres: ['Music', 'Talks'], ...over
    };
  }

  async function build(over: Partial<VideoEditDialogData> = {}): Promise<void> {
    dialogRef = jasmine.createSpyObj<MatDialogRef<VideoEditDialogComponent>>('MatDialogRef', ['close']);
    api = jasmine.createSpyObj<VideoLibraryApiService>('VideoLibraryApiService', ['deleteVideo']);
    api.deleteVideo.and.returnValue(of({ success: true }));

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [VideoEditDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: data(over) },
        { provide: VideoLibraryApiService, useValue: api },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(VideoEditDialogComponent);
    component = fixture.componentInstance;
  }

  /** A keydown whose target is `target`, so the guards can be exercised. */
  function keydown(target: HTMLElement): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key: 'Enter' });
    Object.defineProperty(event, 'target', { value: target });
    return event;
  }

  beforeEach(async () => build());

  // ─── Genres and tags ─────────────────────────────────────────────────

  describe('genres and tags', () => {
    it('should always offer Uncategorized first', async () => {
      await build({ availableGenres: ['Music'] });

      expect(component.genres[0]).toBe('Uncategorized');
    });

    it('should not offer Uncategorized twice', async () => {
      await build({ availableGenres: ['Uncategorized', 'Music'] });

      expect(component.genres.filter(g => g === 'Uncategorized').length).toBe(1);
    });

    it('should copy the tags rather than edit the caller\'s array', async () => {
      const tags = ['Live'];
      await build({ tags });

      component.removeTag('Live');

      expect(tags).toEqual(['Live']);
    });

    it('should add a tag from the dropdown', () => {
      component.onGenreSelected('Music');

      expect(component.tags).toEqual(['Music']);
    });

    it('should ignore an empty selection', () => {
      component.onGenreSelected(null);
      component.onGenreSelected('');

      expect(component.tags).toEqual([]);
    });

    it('should not add the same tag twice, whatever the casing', () => {
      component.onGenreSelected('Music');
      component.onGenreSelected('music');

      expect(component.tags).toEqual(['Music']);
    });

    it('should stop offering a genre once it is on', () => {
      expect(component.availableGenres).toEqual(['Music', 'Talks']);

      component.onGenreSelected('Music');

      expect(component.availableGenres).toEqual(['Talks']);
    });

    it('should never offer Uncategorized in the dropdown', () => {
      // It is the absence of a genre, not one you pick.
      expect(component.availableGenres).not.toContain('Uncategorized');
    });

    it('should trim a typed tag and clear the input', () => {
      const chipInput = jasmine.createSpyObj('chipInput', ['clear']);

      component.addTag({ value: '  Live  ', chipInput } as any);

      expect(component.tags).toEqual(['Live']);
      expect(chipInput.clear).toHaveBeenCalled();
    });

    it('should remove a tag', () => {
      component.onGenreSelected('Music');

      component.removeTag('Music');

      expect(component.tags).toEqual([]);
    });
  });

  // ─── Saving ──────────────────────────────────────────────────────────

  describe('saving', () => {
    it('should promote the first tag that is a known genre', () => {
      // Tags are freeform; the primary genre has to be one the library knows,
      // which is what makes it usable as a filter.
      component.tags = ['Live', 'Talks', 'Music'];

      component.onSave();

      expect(dialogRef.close.calls.mostRecent().args[0].primaryGenre).toBe('Talks');
    });

    it('should never promote Uncategorized', () => {
      component.tags = ['Uncategorized', 'Music'];

      component.onSave();

      expect(dialogRef.close.calls.mostRecent().args[0].primaryGenre).toBe('Music');
    });

    it('should leave the primary genre unset when no tag is a genre', () => {
      component.tags = ['Live'];

      component.onSave();

      expect(dialogRef.close.calls.mostRecent().args[0].primaryGenre).toBeNull();
    });

    it('should keep the original title when the box is emptied', () => {
      // A blank title would leave the video unfindable.
      component.title = '   ';
      component.channel = '';

      component.onSave();

      const result = dialogRef.close.calls.mostRecent().args[0];
      expect(result.title).toBe('A Video');
      expect(result.channel).toBe('A Channel');
    });

    it('should trim what was typed', () => {
      component.title = '  New Title  ';

      component.onSave();

      expect(dialogRef.close.calls.mostRecent().args[0].title).toBe('New Title');
    });

    it('should close with nothing on cancel', () => {
      component.onCancel();

      expect(dialogRef.close).toHaveBeenCalledWith();
    });
  });

  // ─── Deleting ────────────────────────────────────────────────────────

  describe('deleting', () => {
    it('should ask before deleting', () => {
      const confirm = spyOn(window, 'confirm').and.returnValue(false);

      component.confirmDelete();

      expect(confirm).toHaveBeenCalled();
      expect(api.deleteVideo).not.toHaveBeenCalled();
    });

    it('should delete and report it once confirmed', () => {
      spyOn(window, 'confirm').and.returnValue(true);

      component.confirmDelete();

      expect(api.deleteVideo).toHaveBeenCalledWith('a.mp4');
      expect(dialogRef.close).toHaveBeenCalledWith({ deleted: true } as any);
    });

    it('should stay open when the delete fails', () => {
      // Closing would tell the user it worked.
      spyOn(window, 'confirm').and.returnValue(true);
      api.deleteVideo.and.returnValue(throwError(() => new Error('busy')));

      component.confirmDelete();

      expect(dialogRef.close).not.toHaveBeenCalled();
      expect(component.isDeleting).toBe(false);
    });

    it('should not fire a second delete over the first', () => {
      spyOn(window, 'confirm').and.returnValue(true);
      api.deleteVideo.and.returnValue(new Observable<{ success: boolean }>(() => {}));

      component.confirmDelete();
      component.confirmDelete();

      expect(api.deleteVideo).toHaveBeenCalledTimes(1);
    });

    /**
     * The defect this pass found.
     *
     * The delete was piped through the component's `destroy$` subject. It is a
     * DELETE, and unsubscribing an HttpClient call aborts the request — so
     * closing the dialog while the delete was in flight cancelled the deletion
     * the user had just confirmed, leaving the video in the library with no
     * error to say so.
     */
    it('should let a delete already in flight survive the dialog closing', () => {
      let aborted = false;
      spyOn(window, 'confirm').and.returnValue(true);
      api.deleteVideo.and.returnValue(
        new Observable<{ success: boolean }>(() => () => { aborted = true; }));
      component.confirmDelete();

      fixture.destroy();

      expect(aborted).toBe(false);
    });
  });

  // ─── The two-step delete ─────────────────────────────────────────────

  describe('the keyboard delete confirmation', () => {
    it('should arm rather than delete on the first press', () => {
      component.initiateDeleteConfirm();

      expect(component.deleteConfirmPending).toBe(true);
      expect(api.deleteVideo).not.toHaveBeenCalled();
    });

    it('should delete on the confirming press', () => {
      component.initiateDeleteConfirm();

      component.handleEnterKey(keydown(document.createElement('div')));

      expect(api.deleteVideo).toHaveBeenCalled();
    });

    it('should disarm itself after five seconds', () => {
      // An armed delete left sitting is a keystroke away from destroying a file.
      jasmine.clock().install();
      try {
        component.initiateDeleteConfirm();

        jasmine.clock().tick(5000);

        expect(component.deleteConfirmPending).toBe(false);
      } finally {
        jasmine.clock().uninstall();
      }
    });

    it('should disarm on Escape without closing the dialog', () => {
      component.initiateDeleteConfirm();

      component.handleEscapeKey(new KeyboardEvent('keydown', { key: 'Escape' }));

      expect(component.deleteConfirmPending).toBe(false);
      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should not leave a timer running past the dialog', () => {
      jasmine.clock().install();
      try {
        component.initiateDeleteConfirm();
        fixture.destroy();

        expect(() => jasmine.clock().tick(10000)).not.toThrow();
      } finally {
        jasmine.clock().uninstall();
      }
    });
  });

  // ─── Keyboard shortcuts ──────────────────────────────────────────────

  describe('keyboard shortcuts', () => {
    it('should save on Enter', () => {
      component.handleEnterKey(keydown(document.createElement('div')));

      expect(dialogRef.close).toHaveBeenCalled();
    });

    it('should leave Enter alone inside the tag box', () => {
      // That is how a tag is committed — saving instead would lose it.
      const input = document.createElement('input');
      input.placeholder = 'Add tag';

      component.handleEnterKey(keydown(input));

      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should leave Enter alone in a textarea', () => {
      component.handleEnterKey(keydown(document.createElement('textarea')));

      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should cancel on Escape', () => {
      component.handleEscapeKey(new KeyboardEvent('keydown', { key: 'Escape' }));

      expect(dialogRef.close).toHaveBeenCalledWith();
    });

    it('should arm the delete on the Delete key', () => {
      component.handleDeleteKey(keydown(document.createElement('div')));

      expect(component.deleteConfirmPending).toBe(true);
    });

    it('should leave the Delete key alone while typing', () => {
      // Otherwise deleting a character in the title arms a file deletion.
      component.handleDeleteKey(keydown(document.createElement('input')));

      expect(component.deleteConfirmPending).toBe(false);
    });
  });

  // ─── Odds and ends ───────────────────────────────────────────────────

  describe('the thumbnail and the YouTube link', () => {
    it('should fall back to the placeholder', async () => {
      await build({ thumbnailUrl: null });

      expect(component.displayThumbnailUrl).toBe('/assets/video-placeholder.jpg');
    });

    it('should swap a broken thumbnail for the placeholder once', () => {
      const img = document.createElement('img');
      img.src = 'http://x/broken.jpg';
      component.onThumbnailError({ target: img } as unknown as Event);
      expect(img.src).toContain('video-placeholder.jpg');

      const before = img.src;
      component.onThumbnailError({ target: img } as unknown as Event);
      expect(img.src).toBe(before);
    });

    it('should open the original on YouTube in a new tab', async () => {
      await build({ youTubeId: 'abc123' });
      const open = spyOn(window, 'open');

      component.openOnYouTube();

      expect(open).toHaveBeenCalledWith('https://www.youtube.com/watch?v=abc123', '_blank');
    });

    it('should do nothing when the video did not come from YouTube', () => {
      const open = spyOn(window, 'open');

      component.openOnYouTube();

      expect(open).not.toHaveBeenCalled();
    });
  });
});
