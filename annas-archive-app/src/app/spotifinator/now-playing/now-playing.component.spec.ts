import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { SpotifyNowPlayingComponent } from './now-playing.component';
import { SpotifyPlaybackService } from '../../services/spotify-playback.service';
import { SpotifyPlaybackState } from '../spotifinator.models';

describe('SpotifyNowPlayingComponent', () => {
  let fixture: ComponentFixture<SpotifyNowPlayingComponent>;
  let component: SpotifyNowPlayingComponent;
  let playback: jasmine.SpyObj<SpotifyPlaybackService>;

  const state = (over: Partial<SpotifyPlaybackState> = {}): SpotifyPlaybackState => ({
    isPlaying: true, isShuffling: false, progressMs: 0, device: null,
    track: {
      id: 't', name: 'Mystery Train', artists: 'Elvis', albumName: 'Sun',
      durationMs: 146000, uri: 'spotify:track:t', albumArtUrl: null, spotifyUrl: null
    },
    ...over
  } as SpotifyPlaybackState);

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SpotifyNowPlayingComponent, NoopAnimationsModule],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    }).compileComponents();

    playback = jasmine.createSpyObj<SpotifyPlaybackService>('SpotifyPlaybackService',
      ['play', 'pause', 'skipNext', 'skipPrevious', 'setShuffle', 'refreshDevices', 'refreshState']);
    TestBed.overrideProvider(SpotifyPlaybackService, { useValue: playback });

    fixture = TestBed.createComponent(SpotifyNowPlayingComponent);
    component = fixture.componentInstance;
  });

  it('shows nothing at all when there is no track and no problem to report', () => {
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.now-playing')).toBeNull();
  });

  it('offers a way back when playback is impossible rather than going quiet', () => {
    // The idle-with-a-reason case: no track, but something the user can act on.
    component.problem = 'Nothing to play on yet.';
    fixture.detectChanges();

    const footer = fixture.nativeElement.querySelector('.now-playing-problem');
    expect(footer.textContent).toContain('Nothing to play on yet.');

    footer.querySelector('button').click();
    expect(playback.refreshDevices).toHaveBeenCalled();
    expect(playback.refreshState).toHaveBeenCalled();
  });

  it('prefers the track over the problem when both are set', () => {
    // A stale problem must not hide music that is audibly playing.
    component.playback = state();
    component.problem = 'Nothing to play on yet.';
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.now-playing-problem')).toBeNull();
    expect(fixture.nativeElement.querySelector('.now-copy strong').textContent)
      .toContain('Mystery Train');
  });

  it('reports progress as a percentage of the track, guarding zero length', () => {
    component.playback = state({ progressMs: 73000 });
    expect(component.progressPercent()).toBe(50);

    // A stream or a track Spotify gave no length for. Dividing by it would put
    // Infinity into a width, which renders as a full bar on an unstarted track.
    component.playback = state({ progressMs: 10, track: { durationMs: 0 } as never });
    expect(component.progressPercent()).toBe(0);
  });

  it('never runs the bar past the end of the track', () => {
    // Spotify's reported position can overshoot the duration it also reported.
    component.playback = state({ progressMs: 200000 });

    expect(component.progressPercent()).toBe(100);
  });

  it('names the state the press will produce, not the one it is in', () => {
    component.playback = state({ isShuffling: false });
    expect(component.shuffleLabel()).toBe('Turn shuffle on');

    component.playback = state({ isShuffling: true });
    expect(component.shuffleLabel()).toBe('Turn shuffle off');
  });

  it('sends the opposite of the current shuffle state, not the current one', () => {
    component.playback = state({ isShuffling: true });

    component.toggleShuffle();

    expect(playback.setShuffle).toHaveBeenCalledWith(false);
  });

  it('turns shuffle on when nothing is playing yet', () => {
    // playback is null before the first state arrives; `!undefined` has to mean
    // "on", or the first press of shuffle does nothing visible.
    component.toggleShuffle();

    expect(playback.setShuffle).toHaveBeenCalledWith(true);
  });

  it('pauses what is playing and resumes what is not', () => {
    component.playback = state({ isPlaying: true });
    component.togglePlayPause();
    expect(playback.pause).toHaveBeenCalled();

    component.playback = state({ isPlaying: false });
    component.togglePlayPause();
    // An empty command resumes whatever the device had, rather than restarting
    // something — there is no context to send from the transport bar.
    expect(playback.play).toHaveBeenCalledWith({});
  });

  it('wires the transport buttons to the service', () => {
    component.playback = state();
    fixture.detectChanges();

    const buttons = fixture.nativeElement.querySelectorAll('.now-transport button');
    expect(buttons.length).toBe(4);
    buttons[1].click();
    buttons[3].click();

    expect(playback.skipPrevious).toHaveBeenCalled();
    expect(playback.skipNext).toHaveBeenCalled();
  });

  it('names the device the sound is coming out of', () => {
    // On the iPads the browser is a remote, so this is the only clue about where
    // the music actually is.
    component.playback = state({
      device: { id: 'd', name: 'Kitchen', type: 'Speaker', isActive: true } as never
    });
    fixture.detectChanges();

    const device = fixture.nativeElement.querySelector('.now-device');
    expect(device.textContent).toContain('Kitchen');
    expect(device.querySelector('mat-icon').textContent.trim()).toBe('speaker');
  });
});
