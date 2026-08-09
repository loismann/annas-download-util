import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { ElementRef } from '@angular/core';

import {
  JellyfinPlayerModalComponent, JellyfinPlayerModalData
} from './jellyfin-player-modal.component';

/**
 * Characterization tests for the video player modal.
 *
 * A review pass in the same series as the library pages. It found the teardown
 * ordering bug in "closing the player" — progress was saved *after* hls.js was
 * destroyed, and hls.js's teardown resets the element's currentTime to zero.
 *
 * The <video> element is faked throughout. A real one in Karma will not decode
 * anything, so every assertion about playback would either be vacuous or
 * flaky; a fake makes what the component actually does to the element visible.
 */
describe('JellyfinPlayerModalComponent (characterization)', () => {
  let component: JellyfinPlayerModalComponent;
  let fixture: ComponentFixture<JellyfinPlayerModalComponent>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<JellyfinPlayerModalComponent>>;
  let saveProgress: jasmine.Spy;

  /** A stand-in for <video> carrying only what this component touches. */
  interface FakeVideo {
    currentTime: number;
    textTracks: { mode: string }[];
    audioTracks?: { length: number; [i: number]: { enabled: boolean; label?: string; language?: string } };
    play: jasmine.Spy;
    canPlayType: jasmine.Spy;
  }

  function fakeVideo(over: Partial<FakeVideo> = {}): FakeVideo {
    return {
      currentTime: 0,
      textTracks: [],
      play: jasmine.createSpy('play'),
      canPlayType: jasmine.createSpy('canPlayType').and.returnValue(''),
      ...over
    };
  }

  /** Builds the component with `data`, then attaches `video` as its element. */
  async function build(data: Partial<JellyfinPlayerModalData>, video?: FakeVideo): Promise<FakeVideo> {
    saveProgress = jasmine.createSpy('saveProgress');
    dialogRef = jasmine.createSpyObj<MatDialogRef<JellyfinPlayerModalComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [JellyfinPlayerModalComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: { title: 'Them!', mode: 'native', saveProgress, ...data } as JellyfinPlayerModalData
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(JellyfinPlayerModalComponent);
    component = fixture.componentInstance;

    const el = video ?? fakeVideo();
    component.videoElRef = new ElementRef(el as unknown as HTMLVideoElement);
    return el;
  }

  // ─── The resume prompt ───────────────────────────────────────────────

  describe('the resume prompt', () => {
    it('should offer to resume a part-watched film', async () => {
      await build({ resumePositionSeconds: 1800, durationSeconds: 5400 });

      expect(component.resumeChoicePending).toBe(true);
    });

    it('should not bother for the first few seconds', async () => {
      // Resuming at 12 seconds is not meaningfully different from starting over,
      // so the prompt would be a click for nothing.
      await build({ resumePositionSeconds: 12, durationSeconds: 5400 });

      expect(component.resumeChoicePending).toBe(false);
    });

    it('should treat a position near the end as already finished', async () => {
      // Otherwise finishing a film means being asked, next time, whether to
      // resume two minutes from the credits.
      await build({ resumePositionSeconds: 5390, durationSeconds: 5400 });

      expect(component.resumeChoicePending).toBe(false);
    });

    it('should still prompt when the duration is unknown', async () => {
      await build({ resumePositionSeconds: 1800, durationSeconds: undefined });

      expect(component.resumeChoicePending).toBe(true);
    });

    it('should never prompt in embed mode', async () => {
      // That is Jellyfin's own player in an iframe; it has its own resume UI.
      await build({ mode: 'embed', embedUrl: 'http://x/embed', resumePositionSeconds: 1800 });

      expect(component.resumeChoicePending).toBe(false);
    });

    it('should format the saved position for the prompt', async () => {
      await build({ resumePositionSeconds: 1394 });
      expect(component.formattedResumeTime()).toBe('23:14');

      await build({ resumePositionSeconds: 3802 });
      expect(component.formattedResumeTime()).toBe('1:03:22');
    });

    it('should seek and play when the user resumes', async () => {
      const video = await build({ resumePositionSeconds: 1800 });
      component.onLoadedMetadata();

      component.resumeFromSaved();

      expect(video.currentTime).toBe(1800);
      expect(video.play).toHaveBeenCalled();
      expect(component.resumeChoicePending).toBe(false);
    });

    it('should start at zero when the user starts over', async () => {
      const video = await build({ resumePositionSeconds: 1800 });
      component.onLoadedMetadata();

      component.restartFromBeginning();

      expect(video.currentTime).toBe(0);
      expect(video.play).toHaveBeenCalled();
    });

    /**
     * A click can land before `loadedmetadata` fires, and setting currentTime
     * on an element that has not loaded its metadata does not reliably stick —
     * so the choice is held and applied once the metadata arrives.
     */
    it('should hold a choice made before the metadata has loaded', async () => {
      const video = await build({ resumePositionSeconds: 1800 });

      component.resumeFromSaved();
      expect(video.currentTime).toBe(0);

      component.onLoadedMetadata();

      expect(video.currentTime).toBe(1800);
      expect(video.play).toHaveBeenCalled();
    });

    it('should not start playing while the prompt is still up', async () => {
      const video = await build({ resumePositionSeconds: 1800 });

      component.onLoadedMetadata();

      expect(video.play).not.toHaveBeenCalled();
      expect(video.currentTime).toBe(0);
    });

    it('should just play from the saved spot when there is no prompt', async () => {
      const video = await build({ resumePositionSeconds: 5 });

      component.onLoadedMetadata();

      expect(video.currentTime).toBe(5);
      expect(video.play).toHaveBeenCalled();
    });
  });

  // ─── Saving progress ─────────────────────────────────────────────────

  describe('saving progress', () => {
    it('should save on pause', async () => {
      const video = await build({});
      video.currentTime = 640;

      component.onPause();

      expect(saveProgress).toHaveBeenCalledWith(640);
    });

    it('should save on a timer while playing', async () => {
      jasmine.clock().install();
      try {
        const video = await build({});
        component.onLoadedMetadata();
        video.currentTime = 100;

        jasmine.clock().tick(15000);

        expect(saveProgress).toHaveBeenCalledWith(100);
      } finally {
        jasmine.clock().uninstall();
      }
    });

    it('should not re-save a position that has not moved', async () => {
      const video = await build({});
      video.currentTime = 640;
      component.onPause();
      saveProgress.calls.reset();

      component.onPause();

      expect(saveProgress).not.toHaveBeenCalled();
    });

    it('should do nothing when the caller gave it nowhere to save', async () => {
      // Embed mode has no progress callback — this must not throw.
      const video = await build({ mode: 'embed', saveProgress: undefined });
      video.currentTime = 100;

      expect(() => component.onPause()).not.toThrow();
    });
  });

  // ─── Closing the player ──────────────────────────────────────────────

  describe('closing the player', () => {
    it('should save the final position', async () => {
      const video = await build({});
      video.currentTime = 2000;

      component.ngOnDestroy();

      expect(saveProgress).toHaveBeenCalledWith(2000);
    });

    it('should stop the save timer', async () => {
      jasmine.clock().install();
      try {
        const video = await build({});
        component.onLoadedMetadata();
        component.ngOnDestroy();
        saveProgress.calls.reset();
        video.currentTime = 999;

        jasmine.clock().tick(60000);

        expect(saveProgress).not.toHaveBeenCalled();
      } finally {
        jasmine.clock().uninstall();
      }
    });

    /**
     * The defect this pass found.
     *
     * hls.js's `destroy()` detaches the media element — it clears the src and
     * calls `load()`, which resets `currentTime` to 0. Progress was saved after
     * that call, so closing a transcoded film recorded position 0 and threw
     * away the resume point. It only affected HLS playback, which is to say the
     * long files that most need resuming: anything Jellyfin had to transcode.
     */
    it('should save the position before tearing down hls.js', async () => {
      const video = await build({ isHls: true, streamUrl: 'http://x/master.m3u8' });
      video.currentTime = 2000;
      // Stands in for hls.js: destroying it detaches the element, which is what
      // zeroes currentTime in the real thing.
      (component as unknown as { hls: { destroy: () => void } }).hls = {
        destroy: () => { video.currentTime = 0; }
      };

      component.ngOnDestroy();

      expect(saveProgress).toHaveBeenCalledWith(2000);
      expect(saveProgress).not.toHaveBeenCalledWith(0);
    });

    it('should close on the close button', async () => {
      await build({});

      component.close();

      expect(dialogRef.close).toHaveBeenCalled();
    });
  });

  // ─── Tracks ──────────────────────────────────────────────────────────

  describe('audio tracks', () => {
    function withAudioTracks(tracks: { enabled: boolean; label?: string; language?: string }[]) {
      const list = { length: tracks.length } as Record<string | number, unknown>;
      tracks.forEach((t, i) => { list[i] = t; });
      return fakeVideo({ audioTracks: list as FakeVideo['audioTracks'] });
    }

    it('should offer nothing to switch between for a single track', async () => {
      await build({}, withAudioTracks([{ enabled: true }]));

      component.onLoadedMetadata();

      expect(component.audioTrackOptions).toEqual([]);
    });

    it('should list several tracks and mark the enabled one', async () => {
      await build({}, withAudioTracks([
        { enabled: false, label: 'English' },
        { enabled: true, label: 'Commentary' }
      ]));

      component.onLoadedMetadata();

      expect(component.audioTrackOptions.map(o => o.label)).toEqual(['English', 'Commentary']);
      expect(component.selectedAudioTrackId).toBe(1);
    });

    /**
     * Observed on some multi-track files: the demuxer enables nothing on its
     * own and playback is silent until a track is turned on explicitly.
     */
    it('should force the first track on when the browser enabled none', async () => {
      const video = withAudioTracks([{ enabled: false }, { enabled: false }]);
      await build({}, video);

      component.onLoadedMetadata();

      expect(video.audioTracks![0].enabled).toBe(true);
      expect(component.selectedAudioTrackId).toBe(0);
    });

    it('should enable exactly one track when switching', async () => {
      const video = withAudioTracks([
        { enabled: true, label: 'A' }, { enabled: false, label: 'B' }, { enabled: false, label: 'C' }
      ]);
      await build({}, video);

      component.selectAudioTrack(2);

      expect([0, 1, 2].map(i => video.audioTracks![i].enabled)).toEqual([false, false, true]);
      expect(component.selectedAudioTrackId).toBe(2);
    });

    it('should cope with a browser that has no audio track API', async () => {
      // Firefox and Safari are inconsistent here, hence the feature detect.
      await build({}, fakeVideo({ audioTracks: undefined }));

      expect(() => component.onLoadedMetadata()).not.toThrow();
      expect(component.audioTrackOptions).toEqual([]);
    });

    it('should name a track by title, then language, then position', async () => {
      await build({});

      expect(component.trackLabel({ index: 1, title: 'Director', language: 'eng' } as any)).toBe('Director');
      expect(component.trackLabel({ index: 1, language: 'eng' } as any)).toBe('eng');
      expect(component.trackLabel({ index: 7 } as any)).toBe('Track 7');
    });
  });

  describe('subtitles', () => {
    it('should show exactly one and disable the rest', async () => {
      const video = fakeVideo({ textTracks: [{ mode: 'disabled' }, { mode: 'disabled' }] });
      await build({}, video);

      component.selectSubtitle(1);

      expect(video.textTracks.map(t => t.mode)).toEqual(['disabled', 'showing']);
      expect(component.selectedSubtitlePos).toBe(1);
    });

    it('should turn them all off for Off', async () => {
      const video = fakeVideo({ textTracks: [{ mode: 'showing' }, { mode: 'disabled' }] });
      await build({}, video);

      component.selectSubtitle(null);

      expect(video.textTracks.map(t => t.mode)).toEqual(['disabled', 'disabled']);
      expect(component.selectedSubtitlePos).toBeNull();
    });

    it('should build a subtitle URL through the caller\'s own addressing', async () => {
      // Movies and episodes address subtitles differently server-side, which is
      // why this is a callback rather than a URL template in here.
      await build({ subtitleUrlFor: (i: number) => `http://x/sub/${i}.vtt` });

      expect(component.subtitleUrl({ index: 3 } as any)).toBe('http://x/sub/3.vtt');
    });

    it('should give an empty URL when the caller supplied no builder', async () => {
      await build({});

      expect(component.subtitleUrl({ index: 3 } as any)).toBe('');
    });
  });

  // ─── Embed mode ──────────────────────────────────────────────────────

  describe('embed mode', () => {
    it('should trust the backend-resolved embed URL', async () => {
      // It is our own proxy deep link, not anything a user typed.
      await build({ mode: 'embed', embedUrl: 'http://x/embed' });

      expect(component.safeEmbedUrl).toBeTruthy();
    });

    it('should not build one for native playback', async () => {
      await build({ mode: 'native', streamUrl: 'http://x/stream' });

      expect(component.safeEmbedUrl).toBeUndefined();
    });

    it('should ignore loadedmetadata in embed mode', async () => {
      const video = await build({ mode: 'embed', embedUrl: 'http://x/e' });

      component.onLoadedMetadata();

      expect(video.play).not.toHaveBeenCalled();
    });
  });
});
