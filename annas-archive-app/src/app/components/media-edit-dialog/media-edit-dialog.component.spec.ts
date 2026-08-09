import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Observable, of, throwError } from 'rxjs';

import { MediaEditDialogComponent, MediaEditDialogData } from './media-edit-dialog.component';
import { MediaLibraryApiService } from '../../services/media-library-api.service';
import { AudiobookApiService } from '../../services/audiobook-api.service';
import { AuthService } from '../../services/auth.service';
import { LoggerService } from '../../services/logger.service';

/**
 * Characterization tests for the shared media edit dialog.
 *
 * One dialog serves three media types with three different backends and two
 * different id types, so most of what is worth pinning here is that each type
 * is routed to the right one — the favourite toggle in particular writes
 * immediately rather than on Save, so a misrouted call is a real edit against
 * the wrong service.
 */
describe('MediaEditDialogComponent (characterization)', () => {
  let fixture: ComponentFixture<MediaEditDialogComponent>;
  let component: MediaEditDialogComponent;
  let dialogRef: jasmine.SpyObj<MatDialogRef<MediaEditDialogComponent>>;
  let mediaApi: jasmine.SpyObj<MediaLibraryApiService>;
  let audiobookApi: jasmine.SpyObj<AudiobookApiService>;
  let ownerName: string | null;

  function data(over: Partial<MediaEditDialogData> = {}): MediaEditDialogData {
    return {
      title: 'Them!', genres: ['Sci-Fi'], owners: ['Mom'], availableGenres: ['Sci-Fi', 'Horror'],
      favoritedBy: [], mediaType: 'movie', id: 1, ...over
    };
  }

  async function build(over: Partial<MediaEditDialogData> = {}): Promise<void> {
    ownerName = 'Paul';
    dialogRef = jasmine.createSpyObj<MatDialogRef<MediaEditDialogComponent>>('MatDialogRef', ['close']);
    mediaApi = jasmine.createSpyObj<MediaLibraryApiService>('MediaLibraryApiService',
      ['setMovieFavorite', 'setTvFavorite']);
    mediaApi.setMovieFavorite.and.returnValue(of({ favorites: ['Paul'] } as any));
    mediaApi.setTvFavorite.and.returnValue(of({ favorites: ['Paul'] } as any));
    audiobookApi = jasmine.createSpyObj<AudiobookApiService>('AudiobookApiService',
      ['setFavorite', 'deleteItem']);
    audiobookApi.setFavorite.and.returnValue(of({ favorites: ['Paul'] } as any));
    audiobookApi.deleteItem.and.returnValue(of(void 0 as any));

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [MediaEditDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: data(over) },
        { provide: MediaLibraryApiService, useValue: mediaApi },
        { provide: AudiobookApiService, useValue: audiobookApi },
        { provide: AuthService, useValue: { getOwnerName: () => ownerName } },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(MediaEditDialogComponent);
    component = fixture.componentInstance;
  }

  beforeEach(async () => build());

  // ─── Editing ─────────────────────────────────────────────────────────

  describe('editing', () => {
    it('should copy the incoming lists rather than edit them in place', async () => {
      // The caller keeps showing its own tile behind the dialog; editing its
      // arrays would change it before anything was saved, and would survive a
      // cancel.
      const genres = ['Sci-Fi'];
      const owners = ['Mom'];
      await build({ genres, owners });

      component.genres.push('Horror');
      component.selectedOwners.push('Dad');

      expect(genres).toEqual(['Sci-Fi']);
      expect(owners).toEqual(['Mom']);
    });

    it('should hand back the edited lists on save', () => {
      component.genres = ['Horror'];
      component.selectedOwners = ['Dad'];

      component.onSave();

      const result = dialogRef.close.calls.mostRecent().args[0];
      expect(result!.genres).toEqual(['Horror']);
      expect(result!.owners).toEqual(['Dad']);
    });

    it('should close with nothing on cancel', () => {
      component.onCancel();

      expect(dialogRef.close).toHaveBeenCalledWith();
    });
  });

  // ─── Renaming ────────────────────────────────────────────────────────

  describe('renaming', () => {
    it('should send a changed title for an audiobook', async () => {
      await build({ mediaType: 'audiobook', id: 'abs-1', title: 'Old' });
      component.titleInput = '  New  ';

      component.onSave();

      expect(dialogRef.close.calls.mostRecent().args[0]!.title).toBe('New');
    });

    it('should send nothing when the title was not touched', async () => {
      await build({ mediaType: 'audiobook', id: 'abs-1', title: 'Old' });

      component.onSave();

      expect(dialogRef.close.calls.mostRecent().args[0]!.title).toBeUndefined();
    });

    it('should send nothing for a blanked title', async () => {
      await build({ mediaType: 'audiobook', id: 'abs-1', title: 'Old' });
      component.titleInput = '   ';

      component.onSave();

      expect(dialogRef.close.calls.mostRecent().args[0]!.title).toBeUndefined();
    });

    it('should never rename a movie or a show', async () => {
      // Sonarr and Radarr own those names; editing one here would only drift
      // from what they report on the next load.
      component.titleInput = 'A Different Name';

      component.onSave();

      expect(dialogRef.close.calls.mostRecent().args[0]!.title).toBeUndefined();
    });
  });

  // ─── Favourites ──────────────────────────────────────────────────────

  describe('favourites', () => {
    it('should route a movie, a show and an audiobook to their own service', async () => {
      component.toggleFavorite(true);
      expect(mediaApi.setMovieFavorite).toHaveBeenCalledWith(1, true);

      await build({ mediaType: 'tv', id: 9 });
      component.toggleFavorite(true);
      expect(mediaApi.setTvFavorite).toHaveBeenCalledWith(9, true);

      await build({ mediaType: 'audiobook', id: 'abs-1' });
      component.toggleFavorite(true);
      expect(audiobookApi.setFavorite).toHaveBeenCalledWith('abs-1', true);
    });

    it('should fill the star straight away and take the server\'s answer after', () => {
      mediaApi.setMovieFavorite.and.returnValue(of({ favorites: ['Paul', 'Mom'] } as any));

      component.toggleFavorite(true);

      expect(component.data.favoritedBy).toEqual(['Paul', 'Mom']);
      expect(component.isFavorited).toBe(true);
    });

    it('should put the star back when the save fails', () => {
      mediaApi.setMovieFavorite.and.returnValue(throwError(() => new Error('nope')));

      component.toggleFavorite(true);

      expect(component.data.favoritedBy).toEqual([]);
      expect(component.isFavorited).toBe(false);
    });

    it('should put it back the other way when un-favouriting fails', async () => {
      await build({ favoritedBy: ['Paul'] });
      mediaApi.setMovieFavorite.and.returnValue(throwError(() => new Error('nope')));

      component.toggleFavorite(false);

      expect(component.data.favoritedBy).toEqual(['Paul']);
    });

    it('should report only this person\'s own favourite', async () => {
      await build({ favoritedBy: ['Mom'] });

      expect(component.isFavorited).toBe(false);
    });

    it('should do nothing for a session with no name', () => {
      ownerName = null;

      component.toggleFavorite(true);

      expect(mediaApi.setMovieFavorite).not.toHaveBeenCalled();
      expect(component.isFavorited).toBe(false);
    });
  });

  // ─── Deleting an audiobook ───────────────────────────────────────────

  describe('deleting an audiobook', () => {
    beforeEach(async () => build({ mediaType: 'audiobook', id: 'abs-1' }));

    it('should ask first, because this removes the audio files', () => {
      const confirm = spyOn(window, 'confirm').and.returnValue(false);

      component.confirmDelete();

      expect(confirm).toHaveBeenCalled();
      expect(audiobookApi.deleteItem).not.toHaveBeenCalled();
    });

    it('should delete and report it once confirmed', () => {
      spyOn(window, 'confirm').and.returnValue(true);

      component.confirmDelete();

      expect(audiobookApi.deleteItem).toHaveBeenCalledWith('abs-1');
      expect(dialogRef.close.calls.mostRecent().args[0]!.deleted).toBe(true);
    });

    it('should stay open and say why when the delete fails', () => {
      spyOn(window, 'confirm').and.returnValue(true);
      audiobookApi.deleteItem.and.returnValue(throwError(() => new Error('busy')));

      component.confirmDelete();

      expect(dialogRef.close).not.toHaveBeenCalled();
      expect(component.deleteError).toContain('Could not delete');
      expect(component.isDeleting).toBe(false);
    });

    it('should not fire a second delete over the first', () => {
      spyOn(window, 'confirm').and.returnValue(true);
      audiobookApi.deleteItem.and.returnValue(new Observable<void>(() => {}));

      component.confirmDelete();
      component.confirmDelete();

      expect(audiobookApi.deleteItem).toHaveBeenCalledTimes(1);
    });

    it('should refuse for a movie or a show', async () => {
      // There is no equivalent cascade for those, and the caller has its own
      // confirm-then-delete flow for them.
      await build({ mediaType: 'movie', id: 1 });
      const confirm = spyOn(window, 'confirm');

      component.confirmDelete();

      expect(confirm).not.toHaveBeenCalled();
    });

    it('should let a delete already in flight survive the dialog closing', () => {
      let aborted = false;
      spyOn(window, 'confirm').and.returnValue(true);
      audiobookApi.deleteItem.and.returnValue(
        new Observable<void>(() => () => { aborted = true; }));
      component.confirmDelete();

      fixture.destroy();

      expect(aborted).toBe(false);
    });
  });
});
