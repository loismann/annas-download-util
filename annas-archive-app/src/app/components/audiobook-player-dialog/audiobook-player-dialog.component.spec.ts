import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { ElementRef } from '@angular/core';
import { of, throwError } from 'rxjs';

import { AudiobookPlayerDialogComponent } from './audiobook-player-dialog.component';
import { AudiobookApiService, AudiobookItem } from '../../services/audiobook-api.service';
import { AuthService } from '../../services/auth.service';
import { LoggerService } from '../../services/logger.service';

/**
 * Characterization tests for the audiobook player.
 *
 * A review pass in the same series as the library pages. It found the stale
 * `loadedmetadata` listener in "moving between files" — a scrub across two file
 * boundaries left the previous file's handler attached, and it fired on the new
 * file, seeking it to the wrong place.
 *
 * A book is many files but one timeline, so nearly everything here is about the
 * translation between the two. The <audio> element is faked: a real one decodes
 * nothing in Karma, so every assertion would be vacuous.
 */
describe('AudiobookPlayerDialogComponent (characterization)', () => {
  let fixture: ComponentFixture<AudiobookPlayerDialogComponent>;
  let component: AudiobookPlayerDialogComponent;
  let api: jasmine.SpyObj<AudiobookApiService>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<AudiobookPlayerDialogComponent>>;
  let audio: FakeAudio;
  let ownerName: string | null;

  /** Records listeners so a `loadedmetadata` can be fired on demand. */
  class FakeAudio {
    src = '';
    currentTime = 0;
    playbackRate = 1;
    paused = true;
    play = jasmine.createSpy('play').and.callFake(() => { this.paused = false; });
    pause = jasmine.createSpy('pause').and.callFake(() => { this.paused = true; });
    listeners: (() => void)[] = [];

    addEventListener(_type: string, fn: () => void): void { this.listeners.push(fn); }
    removeEventListener(_type: string, fn: () => void): void {
      this.listeners = this.listeners.filter(l => l !== fn);
    }
    /** Fires loadedmetadata for every handler still attached. */
    fireLoadedMetadata(): void { [...this.listeners].forEach(l => l()); }
  }

  /** A two-file book: 100 s then 200 s, with two chapters. */
  function book(over: Partial<AudiobookItem> = {}): AudiobookItem {
    return {
      id: 'abs-1',
      favorites: [],
      progress: {},
      media: {
        duration: 300,
        coverPath: '/covers/1.jpg',
        metadata: { title: 'Dune', authorName: 'Frank Herbert', narratorName: 'A Narrator' },
        audioFiles: [{ ino: 'f1', duration: 100 }, { ino: 'f2', duration: 200 }],
        chapters: [{ start: 0, end: 150, title: 'One' }, { start: 150, end: 300, title: 'Two' }]
      },
      ...over
    } as unknown as AudiobookItem;
  }

  async function build(item: AudiobookItem = book()): Promise<void> {
    ownerName = 'Paul';
    api = jasmine.createSpyObj<AudiobookApiService>('AudiobookApiService',
      ['getStreamUrl', 'getCoverUrl', 'saveProgress', 'setFavorite']);
    api.getStreamUrl.and.callFake((id: string, ino: string) => `http://x/${id}/${ino}`);
    api.getCoverUrl.and.callFake((id: string) => `http://x/cover/${id}`);
    api.saveProgress.and.returnValue(of(void 0 as any));
    api.setFavorite.and.returnValue(of({ favorites: ['Paul'] } as any));
    dialogRef = jasmine.createSpyObj<MatDialogRef<AudiobookPlayerDialogComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [AudiobookPlayerDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: { item } },
        { provide: AudiobookApiService, useValue: api },
        { provide: AuthService, useValue: { getOwnerName: () => ownerName } },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(AudiobookPlayerDialogComponent);
    component = fixture.componentInstance;
    audio = new FakeAudio();
    component.audioElRef = new ElementRef(audio as unknown as HTMLAudioElement);
  }

  beforeEach(async () => build());

  // ─── One timeline over many files ────────────────────────────────────

  describe('the whole-book timeline', () => {
    it('should take the total duration from the book', () => {
      component.ngOnInit();

      expect(component.totalDuration).toBe(300);
    });

    it('should add the file durations up when the book does not say', async () => {
      const item = book();
      (item.media as any).duration = undefined;
      await build(item);

      component.ngOnInit();

      expect(component.totalDuration).toBe(300);
    });

    it('should report position across the whole book, not within the file', () => {
      // The scrub bar is the book; the element only knows about one file.
      component.ngOnInit();
      component.ngAfterViewInit();
      component.onSeek(150);
      audio.fireLoadedMetadata();

      audio.currentTime = 60;
      component.onTimeUpdate();

      expect(component.globalTime).toBe(160);
    });

    it('should name the chapter the position falls in', () => {
      component.ngOnInit();

      component.globalTime = 10;
      expect(component.currentChapter?.title).toBe('One');

      component.globalTime = 200;
      expect(component.currentChapter?.title).toBe('Two');
    });

    it('should treat a chapter boundary as the start of the next one', () => {
      component.ngOnInit();
      component.globalTime = 150;

      expect(component.currentChapter?.title).toBe('Two');
    });

    it('should format hours only when there are hours', () => {
      component.ngOnInit();

      component.globalTime = 61;
      expect(component.formattedCurrentTime).toBe('1:01');

      component.globalTime = 3661;
      expect(component.formattedCurrentTime).toBe('1:01:01');
    });

    it('should show nought rather than NaN before anything has loaded', () => {
      component.globalTime = NaN;

      expect(component.formattedCurrentTime).toBe('0:00');
    });
  });

  // ─── Moving between files ────────────────────────────────────────────

  describe('moving between files', () => {
    beforeEach(() => {
      component.ngOnInit();
      component.ngAfterViewInit();
      audio.fireLoadedMetadata();
    });

    it('should start on the first file', () => {
      expect(component.currentFileIndex).toBe(0);
      expect(audio.src).toBe('http://x/abs-1/f1');
    });

    it('should seek within the current file without reloading', () => {
      const srcBefore = audio.src;

      component.onSeek(50);

      expect(audio.src).toBe(srcBefore);
      expect(audio.currentTime).toBe(50);
      expect(component.globalTime).toBe(50);
    });

    it('should switch file and seek to the offset within it', () => {
      component.onSeek(250);
      audio.fireLoadedMetadata();

      expect(component.currentFileIndex).toBe(1);
      expect(audio.src).toBe('http://x/abs-1/f2');
      // 250 global − 100 s of file one.
      expect(audio.currentTime).toBe(150);
    });

    /**
     * The defect this pass found.
     *
     * The seek is applied from a `loadedmetadata` handler, because setting
     * currentTime before the element has metadata does not stick. Each load
     * added one, and a load superseded before its metadata arrived left its
     * handler attached to run on the file that replaced it.
     *
     * The position survived that — handlers fire in registration order, so the
     * newest has the last word, and asserting on currentTime here would pass
     * either way. `autoplay` did not: reaching the end of a file queues an
     * autoplaying load of the next one, so a listener scrubbing somewhere else
     * instead had playback start under them.
     */
    it('should not let a superseded load start playback', () => {
      component.onEnded();   // queues an autoplaying load of file two
      component.onSeek(20);  // the listener scrubs back into file one instead

      audio.fireLoadedMetadata();

      expect(component.currentFileIndex).toBe(0);
      expect(audio.currentTime).toBe(20);
      expect(audio.play).not.toHaveBeenCalled();
    });

    it('should roll on to the next file when one ends', () => {
      component.onEnded();
      audio.fireLoadedMetadata();

      expect(component.currentFileIndex).toBe(1);
      expect(audio.play).toHaveBeenCalled();
    });

    it('should stop at the end of the last file', () => {
      component.onSeek(250);
      audio.fireLoadedMetadata();
      audio.play.calls.reset();

      component.onEnded();

      expect(component.playing).toBe(false);
      expect(audio.play).not.toHaveBeenCalled();
    });

    it('should jump to a chapter', () => {
      component.jumpToChapter({ start: 150, end: 300, title: 'Two' } as any);
      audio.fireLoadedMetadata();

      expect(component.globalTime).toBe(150);
      expect(component.currentFileIndex).toBe(1);
    });

    it('should carry the playback rate across a file change', () => {
      // Otherwise every file boundary silently resets to normal speed.
      component.onRateChange(1.5);

      component.onSeek(250);

      expect(audio.playbackRate).toBe(1.5);
    });
  });

  // ─── Resuming ────────────────────────────────────────────────────────

  describe('resuming', () => {
    it('should open at this person\'s own saved position', async () => {
      await build(book({ progress: { Paul: 250, Mom: 10 } } as any));

      component.ngOnInit();
      component.ngAfterViewInit();
      audio.fireLoadedMetadata();

      expect(component.currentFileIndex).toBe(1);
      // 250 global − 100 s of file one: the position within the file, not its start.
      expect(audio.currentTime).toBe(150);
      expect(component.globalTime).toBe(250);
    });

    it('should start at the beginning for someone with no saved position', async () => {
      await build(book({ progress: { Mom: 250 } } as any));

      component.ngOnInit();
      component.ngAfterViewInit();
      audio.fireLoadedMetadata();

      expect(component.currentFileIndex).toBe(0);
      expect(audio.currentTime).toBe(0);
    });

    it('should start at the beginning for a session with no name', async () => {
      await build(book({ progress: { Paul: 250 } } as any));
      ownerName = null;

      component.ngOnInit();
      component.ngAfterViewInit();
      audio.fireLoadedMetadata();

      expect(component.currentFileIndex).toBe(0);
    });
  });

  // ─── Saving progress ─────────────────────────────────────────────────

  describe('saving progress', () => {
    beforeEach(() => {
      // Installed before ngOnInit: the save timer is created there, and a clock
      // installed afterwards would leave it bound to the real one.
      jasmine.clock().install();
      component.ngOnInit();
      component.ngAfterViewInit();
      audio.fireLoadedMetadata();
    });

    afterEach(() => jasmine.clock().uninstall());

    it('should save the whole-book position on pause', () => {
      component.globalTime = 123;

      component.onPause();

      expect(api.saveProgress).toHaveBeenCalledWith('abs-1', 123);
      expect(component.playing).toBe(false);
    });

    it('should save on a timer', () => {
      component.globalTime = 42;

      jasmine.clock().tick(15000);

      expect(api.saveProgress).toHaveBeenCalledWith('abs-1', 42);
    });

    it('should not re-save a position that has not moved', () => {
      component.globalTime = 42;
      component.onPause();
      api.saveProgress.calls.reset();

      component.onPause();

      expect(api.saveProgress).not.toHaveBeenCalled();
    });

    it('should save the final position when the dialog closes', () => {
      component.globalTime = 999;

      component.ngOnDestroy();

      expect(api.saveProgress).toHaveBeenCalledWith('abs-1', 999);
    });

    it('should stop the timer when the dialog closes', () => {
      component.ngOnDestroy();
      api.saveProgress.calls.reset();
      component.globalTime = 500;

      jasmine.clock().tick(60000);

      expect(api.saveProgress).not.toHaveBeenCalled();
    });

    it('should carry on when a save fails', () => {
      api.saveProgress.and.returnValue(throwError(() => new Error('down')));
      component.globalTime = 10;

      expect(() => component.onPause()).not.toThrow();
    });
  });

  // ─── Transport and presentation ──────────────────────────────────────

  describe('transport', () => {
    beforeEach(() => {
      component.ngOnInit();
      component.ngAfterViewInit();
      audio.fireLoadedMetadata();
    });

    it('should play and pause on the same button', () => {
      component.togglePlay();
      expect(audio.play).toHaveBeenCalled();

      component.togglePlay();
      expect(audio.pause).toHaveBeenCalled();
    });

    it('should offer the usual audiobook speeds', () => {
      expect(component.rates).toEqual([0.75, 1, 1.25, 1.5, 1.75, 2]);
    });

    it('should apply a rate change immediately', () => {
      component.onRateChange(2);

      expect(audio.playbackRate).toBe(2);
      expect(component.playbackRate).toBe(2);
    });

    it('should say so when the audio will not load', () => {
      component.onLoadError();

      expect(component.loadError).toContain('Audiobookshelf');
    });
  });

  describe('presentation', () => {
    it('should read the title, author and narrator out of the nested shape', () => {
      expect(component.title).toBe('Dune');
      expect(component.author).toBe('Frank Herbert');
      expect(component.narrator).toBe('A Narrator');
    });

    it('should fall back for a book with no metadata', async () => {
      await build({ id: 'x', media: {} } as unknown as AudiobookItem);

      expect(component.title).toBe('Untitled');
      expect(component.author).toBeUndefined();
    });

    it('should use the placeholder when there is no cover on disk', async () => {
      const item = book();
      (item.media as any).coverPath = null;
      await build(item);

      expect(component.coverUrl).toBe('/assets/placeholder.jpg');
    });

    it('should build a cover URL when there is one', () => {
      expect(component.coverUrl).toBe('http://x/cover/abs-1');
    });

    it('should close on the close button', () => {
      component.close();

      expect(dialogRef.close).toHaveBeenCalled();
    });
  });

  // ─── Favourites ──────────────────────────────────────────────────────

  describe('favourites', () => {
    it('should fill the star straight away and take the server\'s answer', () => {
      api.setFavorite.and.returnValue(of({ favorites: ['Paul', 'Mom'] } as any));

      component.toggleFavorite();

      expect(api.setFavorite).toHaveBeenCalledWith('abs-1', true);
      expect(component.data.item.favorites).toEqual(['Paul', 'Mom']);
    });

    it('should put it back when the save fails', () => {
      api.setFavorite.and.returnValue(throwError(() => new Error('nope')));

      component.toggleFavorite();

      expect(component.data.item.favorites).toEqual([]);
      expect(component.isFavorited).toBe(false);
    });

    it('should do nothing for a session with no name', () => {
      ownerName = null;

      component.toggleFavorite();

      expect(api.setFavorite).not.toHaveBeenCalled();
    });
  });
});
