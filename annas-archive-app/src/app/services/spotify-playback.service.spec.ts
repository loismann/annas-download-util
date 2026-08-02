import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of, throwError, firstValueFrom } from 'rxjs';
import { SpotifyPlaybackService } from './spotify-playback.service';
import { SpotifinatorApiService } from './spotifinator-api.service';
import { SpotifyDevice } from '../spotifinator/spotifinator.models';

/**
 * Choosing where the sound comes out.
 *
 * The rules that matter are all about not producing a play button that silently
 * does nothing: a restricted device accepts no commands, an absent device needs an
 * explanation rather than a shrug, and iPads cannot run the in-page player at all.
 */
describe('SpotifyPlaybackService', () => {
  let service: SpotifyPlaybackService;
  let api: jasmine.SpyObj<SpotifinatorApiService>;

  const device = (over: Partial<SpotifyDevice> = {}): SpotifyDevice => ({
    id: 'd1', name: 'Kitchen', type: 'Speaker', isActive: false,
    isRestricted: false, volumePercent: 50,
    ...over
  });

  beforeEach(() => {
    api = jasmine.createSpyObj<SpotifinatorApiService>('SpotifinatorApiService', [
      'getDevices', 'getPlaybackState', 'play', 'pause', 'transferPlayback', 'getPlaybackToken',
      'skipNext', 'skipPrevious', 'setShuffle'
    ]);
    api.getDevices.and.returnValue(of([]));
    api.getPlaybackState.and.returnValue(of(null));
    api.play.and.returnValue(of(void 0));
    api.pause.and.returnValue(of(void 0));
    api.transferPlayback.and.returnValue(of(void 0));
    api.skipNext.and.returnValue(of(void 0));
    api.skipPrevious.and.returnValue(of(void 0));
    api.setShuffle.and.returnValue(of(void 0));

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SpotifinatorApiService, useValue: api }
      ]
    });

    service = TestBed.inject(SpotifyPlaybackService);
  });

  afterEach(() => service.dispose());

  // ─── which device ────────────────────────────────────────────────────────

  it('prefers whichever device is already active', async () => {
    api.getDevices.and.returnValue(of([
      device({ id: 'idle', isActive: false }),
      device({ id: 'playing', isActive: true })
    ]));

    await service.play({ uris: ['spotify:track:a'] });

    expect(api.play).toHaveBeenCalledWith(
      jasmine.objectContaining({ deviceId: 'playing' }));
  });

  it('never targets a restricted device', async () => {
    // Spotify lists these but refuses commands for them, so picking one produces a
    // play button that appears to do nothing at all.
    api.getDevices.and.returnValue(of([
      device({ id: 'car', isRestricted: true, isActive: true }),
      device({ id: 'laptop', isRestricted: false })
    ]));

    await service.play({ uris: ['spotify:track:a'] });

    expect(api.play).toHaveBeenCalledWith(
      jasmine.objectContaining({ deviceId: 'laptop' }));
  });

  it('explains itself rather than failing quietly when nothing can play', async () => {
    api.getDevices.and.returnValue(of([]));

    await service.play({ uris: ['spotify:track:a'] });

    expect(api.play).not.toHaveBeenCalled();
    const problem = await firstValueFrom(service.problem);
    expect(problem).toContain('No device is available');
  });

  it('will not fall back to a restricted device when it is the only one', async () => {
    api.getDevices.and.returnValue(of([device({ id: 'car', isRestricted: true })]));

    await service.play({ uris: ['spotify:track:a'] });

    expect(api.play).not.toHaveBeenCalled();
  });

  it('honours an explicitly chosen device over the active one', async () => {
    api.getDevices.and.returnValue(of([device({ id: 'active', isActive: true })]));

    await service.play({ uris: ['spotify:track:a'], deviceId: 'chosen' });

    expect(api.play).toHaveBeenCalledWith(
      jasmine.objectContaining({ deviceId: 'chosen' }));
  });

  // ─── errors people can act on ────────────────────────────────────────────

  it('names Premium as the reason when Spotify refuses playback', async () => {
    api.getDevices.and.returnValue(of([device()]));
    api.play.and.returnValue(throwError(() => ({ status: 403 })));

    await service.play({ uris: ['spotify:track:a'] });

    expect(await firstValueFrom(service.problem)).toContain('Premium');
  });

  it('tells you the device went away rather than reporting a generic failure', async () => {
    api.getDevices.and.returnValue(of([device()]));
    api.play.and.returnValue(throwError(() => ({ status: 404 })));

    await service.play({ uris: ['spotify:track:a'] });

    expect(await firstValueFrom(service.problem)).toContain('no longer available');
  });

  // ─── idle is not an error ────────────────────────────────────────────────

  it('treats nothing-playing as a normal state', async () => {
    api.getPlaybackState.and.returnValue(of(null));

    await service.refreshState();

    expect(await firstValueFrom(service.state)).toBeNull();
    expect(await firstValueFrom(service.problem)).toBeNull();
  });

  // ─── transport: skipping and shuffle ─────────────────────────────────────

  it('skips on the device that is playing, not the one it would start on', async () => {
    // These differ. `play` falls back to the first usable device; a skip must go to
    // whichever device is actually making noise, or "next" changes the track on a
    // speaker in another room while the one you can hear carries on.
    api.getDevices.and.returnValue(of([
      device({ id: 'kitchen' }),
      device({ id: 'office', isActive: true })
    ]));
    api.getPlaybackState.and.returnValue(of({
      device: device({ id: 'office', isActive: true }),
      isPlaying: true, progressMs: 0, track: null, isShuffling: false
    }));
    await service.refreshState();

    await service.skipNext();

    expect(api.skipNext).toHaveBeenCalledWith('office');
  });

  it('skips forward and back through different endpoints', async () => {
    await service.skipPrevious();

    expect(api.skipPrevious).toHaveBeenCalled();
    expect(api.skipNext).not.toHaveBeenCalled();
  });

  it('explains a refused skip rather than swallowing it', async () => {
    api.skipNext.and.returnValue(throwError(() => ({ status: 403 })));

    await service.skipNext();

    expect(await firstValueFrom(service.problem)).toContain('Premium');
  });

  it('moves the shuffle toggle immediately rather than waiting for the next poll', async () => {
    // The poll is up to five seconds out. A toggle that does not move until then
    // reads as broken and gets pressed a second time, undoing itself.
    api.getPlaybackState.and.returnValue(of({
      device: null, isPlaying: true, progressMs: 0, track: null, isShuffling: false
    }));
    await service.refreshState();

    await service.setShuffle(true);

    expect((await firstValueFrom(service.state))!.isShuffling).toBeTrue();
    expect(api.setShuffle).toHaveBeenCalledWith(true, undefined);
  });

  it('puts the shuffle toggle back when Spotify refuses the change', async () => {
    // Without this the button claims shuffle is on while Spotify has it off — worse
    // than not moving at all, because nothing later contradicts it until the poll.
    api.getPlaybackState.and.returnValue(of({
      device: null, isPlaying: true, progressMs: 0, track: null, isShuffling: false
    }));
    await service.refreshState();
    api.setShuffle.and.returnValue(throwError(() => ({ status: 404 })));

    await service.setShuffle(true);

    expect((await firstValueFrom(service.state))!.isShuffling).toBeFalse();
    expect(await firstValueFrom(service.problem)).toContain('no longer available');
  });

  it('can turn shuffle off as well as on', async () => {
    api.getPlaybackState.and.returnValue(of({
      device: null, isPlaying: true, progressMs: 0, track: null, isShuffling: true
    }));
    await service.refreshState();

    await service.setShuffle(false);

    expect(api.setShuffle).toHaveBeenCalledWith(false, undefined);
    expect((await firstValueFrom(service.state))!.isShuffling).toBeFalse();
  });

  // ─── the iPad rule ───────────────────────────────────────────────────────

  it('rules out the in-page player on iPhones and iPads', () => {
    // Spotify does not support the Web Playback SDK in mobile browsers. Getting
    // this wrong means the iPads show play buttons that can never work.
    const ua = (value: string) =>
      spyOnProperty(navigator, 'userAgent', 'get').and.returnValue(value);

    ua('Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15');
    expect(SpotifyPlaybackService.supportsLocalPlayback()).toBe(false);
  });

  it('rules it out on an iPadOS device masquerading as a Mac', () => {
    // iPadOS 13+ reports a Macintosh user agent; touch points are the giveaway.
    spyOnProperty(navigator, 'userAgent', 'get')
      .and.returnValue('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)');
    spyOnProperty(navigator, 'maxTouchPoints', 'get').and.returnValue(5);

    expect(SpotifyPlaybackService.supportsLocalPlayback()).toBe(false);
  });

  it('allows it on a real desktop', () => {
    spyOnProperty(navigator, 'userAgent', 'get')
      .and.returnValue('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)');
    spyOnProperty(navigator, 'maxTouchPoints', 'get').and.returnValue(0);

    expect(SpotifyPlaybackService.supportsLocalPlayback()).toBe(true);
  });

  // ─── remote mode ─────────────────────────────────────────────────────────

  it('says what to do when there is no device and no in-page player', async () => {
    spyOnProperty(navigator, 'userAgent', 'get')
      .and.returnValue('Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)');
    api.getDevices.and.returnValue(of([]));

    await service.initialize();

    expect(await firstValueFrom(service.mode)).toBe('unavailable');
    expect(await firstValueFrom(service.problem)).toContain('Open Spotify on your');
  });

  it('uses remote control on an iPad that has a device available', async () => {
    spyOnProperty(navigator, 'userAgent', 'get')
      .and.returnValue('Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)');
    api.getDevices.and.returnValue(of([device({ id: 'phone', name: "Paul's iPhone" })]));

    await service.initialize();

    expect(await firstValueFrom(service.mode)).toBe('remote');
  });
});
