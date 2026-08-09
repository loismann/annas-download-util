import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { ElementRef } from '@angular/core';

import { VideoPlayerDialogComponent } from './video-player-dialog.component';

/**
 * Characterization tests for the plain video player dialog.
 *
 * Distinct from the Jellyfin player: no resume, no track switching, just a
 * <video> around a stream URL. The keyboard handling is the part worth pinning —
 * Space is bound at the document, so what it declines to act on matters as much
 * as what it does.
 */
describe('VideoPlayerDialogComponent (characterization)', () => {
  let fixture: ComponentFixture<VideoPlayerDialogComponent>;
  let component: VideoPlayerDialogComponent;
  let dialogRef: jasmine.SpyObj<MatDialogRef<VideoPlayerDialogComponent>>;
  let video: { paused: boolean; play: jasmine.Spy; pause: jasmine.Spy; requestFullscreen: jasmine.Spy; addEventListener: jasmine.Spy };
  let handlers: Record<string, (e?: unknown) => void>;

  async function build(youTubeId: string | null = null): Promise<void> {
    dialogRef = jasmine.createSpyObj<MatDialogRef<VideoPlayerDialogComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [VideoPlayerDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: { title: 'A Video', channel: 'A Channel', streamUrl: 'http://x/stream', youTubeId }
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(VideoPlayerDialogComponent);
    component = fixture.componentInstance;

    handlers = {};
    video = {
      paused: true,
      play: jasmine.createSpy('play'),
      pause: jasmine.createSpy('pause'),
      requestFullscreen: jasmine.createSpy('requestFullscreen'),
      addEventListener: jasmine.createSpy('addEventListener').and.callFake(
        (type: string, fn: (e?: unknown) => void) => { handlers[type] = fn; })
    };
    component.videoPlayerRef = new ElementRef(video as unknown as HTMLVideoElement);
  }

  beforeEach(async () => build());

  describe('load state', () => {
    it('should start on the spinner', () => {
      expect(component.isLoading).toBe(true);
      expect(component.hasError).toBe(false);
    });

    it('should stop the spinner once there is data', () => {
      component.ngAfterViewInit();

      handlers['loadeddata']();

      expect(component.isLoading).toBe(false);
    });

    it('should stop the spinner on canplay too', () => {
      // Some formats fire one and not the other; waiting for both would hang
      // the spinner over a video that is already playable.
      component.ngAfterViewInit();

      handlers['canplay']();

      expect(component.isLoading).toBe(false);
    });

    it('should name the likely cause when the video will not play', () => {
      component.ngAfterViewInit();

      handlers['error']();

      expect(component.hasError).toBe(true);
      expect(component.isLoading).toBe(false);
      expect(component.errorMessage).toContain('format');
    });

    it('should cope with no element at all', () => {
      component.videoPlayerRef = undefined as unknown as ElementRef<HTMLVideoElement>;

      expect(() => component.ngAfterViewInit()).not.toThrow();
      expect(() => component.ngOnDestroy()).not.toThrow();
    });
  });

  describe('transport', () => {
    it('should play and pause on the same action', () => {
      component.togglePlayPause();
      expect(video.play).toHaveBeenCalled();

      video.paused = false;
      component.togglePlayPause();
      expect(video.pause).toHaveBeenCalled();
    });

    it('should stop playback when the dialog closes', () => {
      // Audio carrying on behind a closed dialog with no way to stop it.
      component.ngOnDestroy();

      expect(video.pause).toHaveBeenCalled();
    });

    it('should go fullscreen on the video element', () => {
      spyOnProperty(document, 'fullscreenElement').and.returnValue(null);

      component.toggleFullscreen();

      expect(video.requestFullscreen).toHaveBeenCalled();
    });

    it('should come back out of fullscreen', () => {
      spyOnProperty(document, 'fullscreenElement').and.returnValue(document.body);
      const exit = spyOn(document, 'exitFullscreen').and.resolveTo();

      component.toggleFullscreen();

      expect(exit).toHaveBeenCalled();
    });
  });

  describe('keyboard', () => {
    it('should close on Escape', () => {
      component.handleEscapeKey(new KeyboardEvent('keydown', { key: 'Escape' }));

      expect(dialogRef.close).toHaveBeenCalled();
    });

    it('should play and pause on Space', () => {
      const event = new KeyboardEvent('keydown', { key: ' ' });
      Object.defineProperty(event, 'target', { value: document.createElement('div') });

      component.handleSpaceKey(event);

      expect(video.play).toHaveBeenCalled();
    });

    it('should leave Space alone on a button', () => {
      // Space is how a focused button is activated; hijacking it would make the
      // close button toggle playback instead.
      const event = new KeyboardEvent('keydown', { key: ' ' });
      Object.defineProperty(event, 'target', { value: document.createElement('button') });

      component.handleSpaceKey(event);

      expect(video.play).not.toHaveBeenCalled();
    });
  });

  describe('the YouTube link', () => {
    it('should open the original in a new tab', async () => {
      await build('abc123');
      const open = spyOn(window, 'open');

      component.openOnYouTube();

      expect(open).toHaveBeenCalledWith('https://www.youtube.com/watch?v=abc123', '_blank');
    });

    it('should do nothing for a video that did not come from YouTube', () => {
      const open = spyOn(window, 'open');

      component.openOnYouTube();

      expect(open).not.toHaveBeenCalled();
    });
  });
});
