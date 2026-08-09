import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { SpotifyLibraryPaneComponent } from './library-pane.component';
import { SpotifyPlaybackService } from '../../services/spotify-playback.service';
import { SpotifyPlaylist, SpotifyPlaylistItem } from '../spotifinator.models';

describe('SpotifyLibraryPaneComponent', () => {
  let fixture: ComponentFixture<SpotifyLibraryPaneComponent>;
  let component: SpotifyLibraryPaneComponent;
  let http: HttpTestingController;
  let playback: jasmine.SpyObj<SpotifyPlaybackService>;

  const listItem = (over: Partial<SpotifyPlaylistItem> = {}): SpotifyPlaylistItem => ({
    position: 0, kind: 'Track', id: 't', name: 'Mystery Train',
    uri: 'spotify:track:t', artists: 'Elvis', albumName: 'Sun', durationMs: 146000,
    spotifyUrl: null, isLocal: false, addedAt: null, isrc: null, albumArtUrl: null,
    ...over
  });

  const playlist = (over: Partial<SpotifyPlaylist> = {}): SpotifyPlaylist => ({
    id: 'p1', name: 'Road Trip', imageUrl: null, trackCount: 2, spotifyUrl: null,
    contentsAvailable: true, snapshotId: null, ownerId: null, ownerName: null,
    isOwnedByUser: true, isCollaborative: false, isPublic: null,
    uri: 'spotify:playlist:p1', inventoryAt: null,
    ...over
  });

  const page = (over: Record<string, unknown> = {}) => ({
    playlistId: 'p1', items: [listItem()], total: 1, offset: 0, limit: 50,
    hasMore: false, access: 'Available', snapshotId: null, ...over
  });

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SpotifyLibraryPaneComponent, NoopAnimationsModule],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    }).compileComponents();

    playback = jasmine.createSpyObj<SpotifyPlaybackService>('SpotifyPlaybackService', ['play']);
    TestBed.overrideProvider(SpotifyPlaybackService, { useValue: playback });

    fixture = TestBed.createComponent(SpotifyLibraryPaneComponent);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Sets the input the way the template does, so ngOnChanges actually fires. */
  const open = (value: SpotifyPlaylist | null) => {
    fixture.componentRef.setInput('playlist', value);
    fixture.detectChanges();
  };

  // ─── loading contents ────────────────────────────────────────────────────

  it('asks for nothing until a playlist is picked', () => {
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.library-empty')).toBeTruthy();
  });

  it('loads the contents of the playlist it is given', () => {
    open(playlist());

    http.expectOne(req => req.url.endsWith('/playlists/p1/items')).flush(page());
    fixture.detectChanges();

    expect(component.items).toHaveSize(1);
    expect(fixture.nativeElement.querySelector('.track-name').textContent)
      .toContain('Mystery Train');
  });

  it('starts from the top when the playlist changes rather than appending', () => {
    // The second playlist's page-one arriving after the first playlist's would
    // otherwise read as one long list belonging to neither.
    open(playlist());
    http.expectOne(req => req.url.endsWith('/playlists/p1/items')).flush(page());

    open(playlist({ id: 'p2', name: 'Other' }));
    const second = http.expectOne(req => req.url.endsWith('/playlists/p2/items'));
    expect(second.request.params.get('offset')).toBe('0');
    second.flush(page({ playlistId: 'p2', items: [listItem({ id: 'x', name: 'Other song' })] }));

    expect(component.items).toHaveSize(1);
    expect(component.items[0].name).toBe('Other song');
  });

  it('does not lose the playlist you are looking at to a slow response', () => {
    // Click A, click B, then A's page arrives. Without the guard it overwrites B.
    open(playlist({ id: 'a', name: 'A' }));
    const slow = http.expectOne(req => req.url.endsWith('/playlists/a/items'));

    open(playlist({ id: 'b', name: 'B' }));
    const quick = http.expectOne(req => req.url.endsWith('/playlists/b/items'));

    slow.flush(page({ playlistId: 'a', items: [listItem({ name: 'From A' })] }));
    quick.flush(page({ playlistId: 'b', items: [listItem({ name: 'From B' })] }));

    expect(component.items.map(i => i.name)).toEqual(['From B']);
  });

  it('pages from where it got to, not from the start again', () => {
    open(playlist());
    http.expectOne(req => req.url.endsWith('/playlists/p1/items'))
      .flush(page({ total: 3, hasMore: true }));

    component.loadMore();

    const more = http.expectOne(req => req.url.endsWith('/playlists/p1/items'));
    expect(more.request.params.get('offset')).toBe('1');
    more.flush(page({ items: [listItem({ id: 't2', name: 'Second' })], total: 3 }));

    expect(component.items.map(i => i.name)).toEqual(['Mystery Train', 'Second']);
  });

  it('will not fire a second page while the first is still in flight', () => {
    open(playlist());
    const first = http.expectOne(req => req.url.endsWith('/playlists/p1/items'));

    component.loadMore();

    first.flush(page());
  });

  it('reload re-reads the same playlist, which an input alone cannot ask for', () => {
    // A change plan can add to or empty the very list on screen. Its id has not
    // changed, so nothing about the input says anything happened.
    open(playlist());
    http.expectOne(req => req.url.endsWith('/playlists/p1/items')).flush(page());

    component.reload();

    http.expectOne(req => req.url.endsWith('/playlists/p1/items')).flush(page());
    expect(component.items).toHaveSize(1);
  });

  it('says a playlist Spotify will not read is unreadable, not empty', () => {
    // A followed playlist full of music must not look identical to an empty one.
    open(playlist());
    http.expectOne(req => req.url.endsWith('/playlists/p1/items'))
      .flush(page({ items: [], total: 0, access: 'Forbidden' }));
    fixture.detectChanges();

    const note = fixture.nativeElement.querySelector('.library-note').textContent;
    expect(note).toContain('not the same');
    expect(note).not.toContain('0 items');
  });

  it('says a genuinely empty playlist is empty', () => {
    open(playlist());
    http.expectOne(req => req.url.endsWith('/playlists/p1/items'))
      .flush(page({ items: [], total: 0, access: 'Available' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.library-note').textContent)
      .toContain('really is empty');
  });

  it('leaves the pane usable when Spotify fails outright', () => {
    open(playlist());
    http.expectOne(req => req.url.endsWith('/playlists/p1/items'))
      .flush({}, { status: 502, statusText: 'Bad Gateway' });

    expect(component.itemsLoading).toBe(false);

    component.loadMore();
    http.expectOne(req => req.url.endsWith('/playlists/p1/items')).flush(page());
  });

  // ─── what can and cannot be played ───────────────────────────────────────

  it('will not offer to play a local file even though it has a URI', () => {
    // The discriminating case. Spotify gives local files a spotify:local: URI, so
    // a "does it have a URI" check alone would happily offer to play one — and the
    // API cannot play it. The kind is what makes this safe, not the URI.
    component.playbackMode = 'local';
    const local = listItem({ kind: 'Local', isLocal: true, uri: 'spotify:local:Us:Home+Recording::214' });

    expect(local.uri).toBeTruthy();
    expect(component.canPlayItem(local)).toBe(false);
    expect(component.itemUnplayableReason(local)).toContain('Local files cannot be played');
  });

  it('will not offer to play an item that has left Spotify but kept its URI', () => {
    // Same shape of trap: a removed track can still carry the URI it had.
    component.playbackMode = 'local';
    const gone = listItem({ kind: 'Unavailable', uri: 'spotify:track:removed' });

    expect(component.canPlayItem(gone)).toBe(false);
    expect(component.itemUnplayableReason(gone)).toContain('no longer on Spotify');
  });

  it('offers no play buttons at all when nothing can play', () => {
    component.playbackMode = 'unavailable';

    expect(component.canPlayItem(listItem())).toBe(false);
    expect(component.canPlayPlaylist(playlist())).toBe(false);
  });

  it('explains why playback is impossible on a device that cannot do it', () => {
    spyOnProperty(navigator, 'userAgent', 'get')
      .and.returnValue('Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)');
    component.playbackMode = 'unavailable';

    // The iPad case has to name the workaround, not just refuse.
    expect(component.playDisabledReason()).toContain('Open Spotify on your phone');
  });

  it('plays a track inside its playlist so the next song follows', () => {
    // With bare URIs playback stops after one song. The context is what makes
    // clicking track 3 continue into track 4.
    component.playbackMode = 'local';
    open(playlist());
    http.expectOne(req => req.url.endsWith('/playlists/p1/items')).flush(page());

    component.playItem(listItem({ position: 3 }));

    expect(playback.play).toHaveBeenCalledWith({
      contextUri: 'spotify:playlist:p1', offsetPosition: 3
    });
  });

  it('falls back to the bare track when the playlist has no URI to play in', () => {
    component.playbackMode = 'local';
    open(playlist({ uri: null }));
    http.expectOne(req => req.url.endsWith('/playlists/p1/items')).flush(page());

    component.playItem(listItem());

    expect(playback.play).toHaveBeenCalledWith({ uris: ['spotify:track:t'] });
  });

  it('marks the row that is actually sounding, by URI rather than position', () => {
    // The same track can sit at two positions, and the playing one is whichever
    // Spotify says — position would light up the wrong row.
    component.playback = { track: { uri: 'spotify:track:t' } } as never;

    expect(component.isTrackPlaying(listItem({ position: 7 }))).toBe(true);
    expect(component.isTrackPlaying(listItem({ uri: 'spotify:track:other' }))).toBe(false);
  });
});
