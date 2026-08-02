import { Injectable, NgZone } from '@angular/core';
import { BehaviorSubject, Observable, firstValueFrom } from 'rxjs';
import { SpotifinatorApiService } from './spotifinator-api.service';
import { LoggerService } from './logger.service';
import {
  PlaybackMode,
  SpotifyDevice,
  SpotifyPlaybackState,
  SpotifyPlayCommand
} from '../spotifinator/spotifinator.models';

/** Minimal shape of the bits of the Spotify SDK we touch. */
interface SpotifyPlayer {
  connect(): Promise<boolean>;
  disconnect(): void;
  addListener(event: string, callback: (payload: never) => void): void;
}

declare global {
  interface Window {
    Spotify?: { Player: new (options: unknown) => SpotifyPlayer };
    onSpotifyWebPlaybackSDKReady?: () => void;
  }
}

const SDK_URL = 'https://sdk.scdn.co/spotify-player.js';

/**
 * Makes songs play, by whichever route this device supports.
 *
 * Two routes, deliberately: the Web Playback SDK turns a desktop browser tab into a
 * Spotify device and plays audio directly, but **Spotify does not support it on
 * iOS or iPadOS** — no amount of coaxing makes it work in mobile Safari. So on the
 * iPads we fall back to Connect, where this page is a remote control for a device
 * the user already has open.
 *
 * The mode is decided once, up front, so the UI can say what will happen instead of
 * offering a play button that silently does nothing.
 */
@Injectable({ providedIn: 'root' })
export class SpotifyPlaybackService {
  private readonly mode$ = new BehaviorSubject<PlaybackMode>('unavailable');
  private readonly state$ = new BehaviorSubject<SpotifyPlaybackState | null>(null);
  private readonly devices$ = new BehaviorSubject<SpotifyDevice[]>([]);
  private readonly problem$ = new BehaviorSubject<string | null>(null);

  private player: SpotifyPlayer | null = null;
  private localDeviceId: string | null = null;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private initialized = false;

  constructor(
    private api: SpotifinatorApiService,
    private logger: LoggerService,
    private zone: NgZone
  ) {}

  get mode(): Observable<PlaybackMode> { return this.mode$.asObservable(); }
  get state(): Observable<SpotifyPlaybackState | null> { return this.state$.asObservable(); }
  get devices(): Observable<SpotifyDevice[]> { return this.devices$.asObservable(); }
  get problem(): Observable<string | null> { return this.problem$.asObservable(); }

  get currentMode(): PlaybackMode { return this.mode$.value; }
  get currentState(): SpotifyPlaybackState | null { return this.state$.value; }

  /**
   * Spotify's own guidance: the SDK is not supported in mobile browsers. Checking
   * for touch alone would wrongly exclude touchscreen laptops, so this tests for
   * the platforms Spotify actually excludes.
   */
  static supportsLocalPlayback(): boolean {
    const ua = navigator.userAgent;
    const isIos = /iPad|iPhone|iPod/.test(ua)
      // iPadOS 13+ reports itself as a Mac; the touch points give it away.
      || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1);

    return !isIos && !/Android/.test(ua);
  }

  async initialize(): Promise<void> {
    if (this.initialized) return;
    this.initialized = true;

    if (SpotifyPlaybackService.supportsLocalPlayback()) {
      try {
        await this.startLocalPlayer();
        this.mode$.next('local');
      } catch (error) {
        // Falling back rather than failing: Connect still works, and a desktop
        // user with another device open should not be left with dead buttons.
        this.logger.warn('[Playback] In-page player unavailable, falling back to Connect', error);
        await this.useRemoteMode();
      }
    } else {
      await this.useRemoteMode();
    }

    this.startPolling();
  }

  private async useRemoteMode(): Promise<void> {
    const devices = await this.refreshDevices();
    this.mode$.next(devices.length > 0 ? 'remote' : 'unavailable');

    if (devices.length === 0) {
      this.problem$.next(
        'Nothing to play on yet. Open Spotify on your phone, computer, or a speaker '
        + 'and it will appear here.');
    }
  }

  private startLocalPlayer(): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const timeout = setTimeout(
        () => reject(new Error('The Spotify player did not start in time.')), 15000);

      const create = () => {
        if (!window.Spotify) {
          clearTimeout(timeout);
          reject(new Error('The Spotify SDK did not load.'));
          return;
        }

        const player = new window.Spotify.Player({
          name: "Spotifinator",
          volume: 0.7,
          // The SDK asks for a token whenever it needs one, including on refresh,
          // so this callback is the whole token lifecycle.
          getOAuthToken: (callback: (token: string) => void) => {
            firstValueFrom(this.api.getPlaybackToken())
              .then(result => callback(result.accessToken))
              .catch(error => this.logger.error('[Playback] Could not get a token', error));
          }
        });

        player.addListener('ready', (payload: never) => {
          const { device_id } = payload as unknown as { device_id: string };
          clearTimeout(timeout);

          // SDK callbacks fire outside Angular; without re-entering the zone the
          // now-playing bar would update its data and never re-render.
          this.zone.run(() => {
            this.localDeviceId = device_id;
            this.problem$.next(null);
            resolve();
          });
        });

        player.addListener('not_ready', () => {
          this.zone.run(() => this.localDeviceId = null);
        });

        for (const failure of ['initialization_error', 'authentication_error', 'account_error']) {
          player.addListener(failure, (payload: never) => {
            const { message } = payload as unknown as { message: string };
            clearTimeout(timeout);
            this.zone.run(() => {
              // account_error is nearly always "this account is not Premium", and
              // saying so beats a generic failure the user cannot act on.
              this.problem$.next(failure === 'account_error'
                ? 'In-browser playback needs Spotify Premium on the connected account.'
                : message);
              reject(new Error(message));
            });
          });
        }

        this.player = player;
        player.connect().catch(reject);
      };

      if (window.Spotify) { create(); return; }

      window.onSpotifyWebPlaybackSDKReady = () => this.zone.run(create);

      if (!document.querySelector(`script[src="${SDK_URL}"]`)) {
        const script = document.createElement('script');
        script.src = SDK_URL;
        script.async = true;
        script.onerror = () => {
          clearTimeout(timeout);
          reject(new Error('The Spotify player script could not be loaded.'));
        };
        document.head.appendChild(script);
      }
    });
  }

  async refreshDevices(): Promise<SpotifyDevice[]> {
    try {
      const devices = await firstValueFrom(this.api.getDevices());
      this.devices$.next(devices);
      return devices;
    } catch (error) {
      this.logger.warn('[Playback] Could not list devices', error);
      return [];
    }
  }

  /**
   * Starts something playing. In local mode this tab is the target; in remote mode
   * it is the active device, or the only one, or nothing — and "nothing" is an
   * explanation, not a silent no-op.
   */
  async play(command: SpotifyPlayCommand): Promise<void> {
    const deviceId = command.deviceId ?? await this.resolveTargetDevice();

    if (!deviceId) {
      this.problem$.next(
        'No device is available to play on. Open Spotify on your phone, computer, '
        + 'or a speaker, then try again.');
      return;
    }

    try {
      await firstValueFrom(this.api.play({ ...command, deviceId }));
      this.problem$.next(null);
      // Spotify needs a moment before /me/player reflects the change.
      setTimeout(() => this.refreshState(), 700);
    } catch (error) {
      this.problem$.next(this.describe(error));
    }
  }

  async pause(): Promise<void> {
    try {
      await firstValueFrom(this.api.pause(this.state$.value?.device?.id ?? undefined));
      setTimeout(() => this.refreshState(), 500);
    } catch (error) {
      this.problem$.next(this.describe(error));
    }
  }

  /**
   * Skipping and shuffle both act on whatever device is currently playing, which
   * is not necessarily the one this page would *start* something on. Passing the
   * active device rather than re-resolving a target keeps "next" from silently
   * skipping on a different speaker than the one making noise.
   */
  async skipNext(): Promise<void> {
    await this.command(() => this.api.skipNext(this.activeDeviceId()));
  }

  async skipPrevious(): Promise<void> {
    await this.command(() => this.api.skipPrevious(this.activeDeviceId()));
  }

  async setShuffle(state: boolean): Promise<void> {
    // Optimistic: the poll is up to five seconds away, and a toggle that does not
    // move until then reads as broken and gets pressed twice.
    const previous = this.state$.value;
    if (previous) this.state$.next({ ...previous, isShuffling: state });

    await this.command(
      () => this.api.setShuffle(state, this.activeDeviceId()),
      () => { if (previous) this.state$.next(previous); });
  }

  /** The device Spotify says is playing, if any — not a device we might play on. */
  private activeDeviceId(): string | undefined {
    return this.state$.value?.device?.id ?? undefined;
  }

  private async command(
    send: () => Observable<void>, revert?: () => void
  ): Promise<void> {
    try {
      await firstValueFrom(send());
      this.problem$.next(null);
      setTimeout(() => this.refreshState(), 700);
    } catch (error) {
      revert?.();
      this.problem$.next(this.describe(error));
    }
  }

  async transferTo(deviceId: string): Promise<void> {
    try {
      await firstValueFrom(this.api.transferPlayback(deviceId, true));
      setTimeout(() => this.refreshState(), 700);
    } catch (error) {
      this.problem$.next(this.describe(error));
    }
  }

  private async resolveTargetDevice(): Promise<string | null> {
    if (this.localDeviceId) return this.localDeviceId;

    const devices = this.devices$.value.length > 0
      ? this.devices$.value
      : await this.refreshDevices();

    // A restricted device accepts no commands, so offering it produces a play
    // button that appears to do nothing at all.
    const usable = devices.filter(device => !device.isRestricted);
    return usable.find(device => device.isActive)?.id ?? usable[0]?.id ?? null;
  }

  async refreshState(): Promise<void> {
    try {
      const state = await firstValueFrom(this.api.getPlaybackState());
      this.state$.next(state);
    } catch (error) {
      this.logger.warn('[Playback] Could not read playback state', error);
    }
  }

  /**
   * Polls rather than trusting the SDK's own events, because in remote mode there
   * are none — playback is happening on another device entirely.
   */
  private startPolling(): void {
    this.refreshState();
    this.stopPolling();

    // Outside Angular so a 5-second timer does not trigger change detection for
    // the whole application on every tick.
    this.zone.runOutsideAngular(() => {
      this.pollTimer = setInterval(
        () => this.zone.run(() => this.refreshState()), 5000);
    });
  }

  private stopPolling(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  private describe(error: unknown): string {
    const body = (error as { error?: { message?: string } })?.error;
    const status = (error as { status?: number })?.status;

    if (status === 403) {
      return 'Spotify refused that. Playback through the API needs Spotify Premium.';
    }
    if (status === 404) {
      return 'That device is no longer available. Open Spotify on it and try again.';
    }

    return body?.message ?? 'That could not be played.';
  }

  dispose(): void {
    this.stopPolling();
    this.player?.disconnect();
    this.player = null;
    this.localDeviceId = null;
    this.initialized = false;
  }
}
