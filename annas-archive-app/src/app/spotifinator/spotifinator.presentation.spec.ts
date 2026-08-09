import { SpotifinatorPresentation as Present } from './spotifinator.presentation';
import { SpotifyPlaylist, SpotifyPlaylistItem } from './spotifinator.models';

/**
 * The Spotifinator screen's vocabulary, tested as the pure functions it is.
 *
 * These assertions used to run inside the component suite, which meant standing
 * up a TestBed, an HttpClient and a router to ask what a playlist with an
 * unknown track count should read as. Nothing here touches component state, so
 * nothing here needs any of that.
 */
describe('SpotifinatorPresentation', () => {
  it('formats a duration as minutes and padded seconds', () => {
    expect(Present.formatDuration(180000)).toBe('3:00');
    expect(Present.formatDuration(65000)).toBe('1:05');
    expect(Present.formatDuration(30000)).toBe('0:30');
  });

  // ─── "unknown is not zero" ─────────────────────────────────────────────────

  describe('itemCountLabel', () => {
    const playlist = (over: Partial<SpotifyPlaylist>): SpotifyPlaylist => ({
      id: 'p', name: 'P', imageUrl: null, trackCount: 0, spotifyUrl: null,
      contentsAvailable: true, snapshotId: null, ownerId: null, ownerName: null,
      isOwnedByUser: false, isCollaborative: false, isPublic: null, uri: null, inventoryAt: null,
      ...over
    });

    it('shows a real count when Spotify reported one', () => {
      expect(Present.itemCountLabel(playlist({ trackCount: 17 }))).toBe('17 items');
    });

    it('singularises a one-item playlist', () => {
      expect(Present.itemCountLabel(playlist({ trackCount: 1 }))).toBe('1 item');
    });

    it('says empty playlists have 0 items', () => {
      expect(Present.itemCountLabel(playlist({ trackCount: 0 }))).toBe('0 items');
    });

    it('never renders a number when contents are unavailable', () => {
      // The headline bug: a followed playlist full of music must not read as 0.
      const label = Present.itemCountLabel(
        playlist({ trackCount: null, contentsAvailable: false }));

      expect(label).toBe('Contents unavailable');
      expect(label).not.toContain('0');
    });

    it('trusts contentsAvailable=false even if a count leaked through', () => {
      expect(Present.itemCountLabel(playlist({ trackCount: 5, contentsAvailable: false })))
        .toBe('Contents unavailable');
    });
  });

  describe('ownershipLabel', () => {
    const playlist = (over: Partial<SpotifyPlaylist>): SpotifyPlaylist => ({
      id: 'p', name: 'P', imageUrl: null, trackCount: 0, spotifyUrl: null,
      contentsAvailable: true, snapshotId: null, ownerId: null, ownerName: null,
      isOwnedByUser: false, isCollaborative: false, isPublic: null, uri: null, inventoryAt: null,
      ...over
    });

    it('marks playlists you own', () => {
      expect(Present.ownershipLabel(playlist({ isOwnedByUser: true }))).toBe('Yours');
    });

    it('marks collaborative playlists', () => {
      expect(Present.ownershipLabel(playlist({ isCollaborative: true }))).toBe('Collaborative');
    });

    it('names the owner of a followed playlist', () => {
      expect(Present.ownershipLabel(playlist({ ownerName: 'Mom' }))).toBe('Followed · Mom');
    });

    it('still says followed when the owner name is missing', () => {
      expect(Present.ownershipLabel(playlist({}))).toBe('Followed');
    });
  });

  // ─── item rendering ────────────────────────────────────────────────────────

  describe('itemMeta', () => {
    const item = (over: Partial<SpotifyPlaylistItem>): SpotifyPlaylistItem => ({
      position: 0, kind: 'Track', id: 't', name: 'Song', uri: 'spotify:track:t',
      artists: 'Artist', albumName: 'Album', durationMs: 180000, spotifyUrl: null,
      isLocal: false, addedAt: null, isrc: null, albumArtUrl: null,
      ...over
    });

    it('describes a track with artist, album and duration', () => {
      expect(Present.itemMeta(item({}))).toBe('Artist · Album · 3:00');
    });

    it('omits the empty artist line for a podcast episode', () => {
      expect(Present.itemMeta(item({ kind: 'Episode', artists: '', albumName: null })))
        .toBe('3:00');
    });

    it('flags a local file', () => {
      expect(Present.itemMeta(item({ kind: 'Local' }))).toContain('local file');
    });

    it('explains an item that is no longer on Spotify', () => {
      expect(Present.itemMeta(item({ kind: 'Unavailable', artists: '', albumName: null, durationMs: 0 })))
        .toBe('This item is no longer on Spotify');
    });
  });

  describe('type guards', () => {
    it('tells an items page apart from a playlist', () => {
      const page = { playlistId: 'p', items: [], total: 0, offset: 0, limit: 50,
                     hasMore: false, access: 'Available', snapshotId: null };

      expect(Present.isItemsPage(page)).toBe(true);
      expect(Present.isPlaylist(page)).toBe(false);
    });

    it('tells a playlist apart from an items page', () => {
      const playlist = { id: 'p', name: 'P', contentsAvailable: true, trackCount: 3 };

      expect(Present.isPlaylist(playlist)).toBe(true);
      expect(Present.isItemsPage(playlist)).toBe(false);
    });

    it('does not mistake an empty array for results', () => {
      expect(Present.isPlaylistArray([])).toBe(false);
      expect(Present.isRecentContexts([])).toBe(false);
    });

    it('treats null data as nothing to render', () => {
      expect(Present.isPlaylist(null)).toBe(false);
      expect(Present.isItemsPage(null)).toBe(false);
      expect(Present.isSearchResult(null)).toBe(false);
    });

    it('recognizes inventory progress without mistaking it for analysis', () => {
      const status = {
        jobId: 'job', state: 'Running', totalPlaylists: 100, processedPlaylists: 25,
        readablePlaylists: 24, partialPlaylists: 1, unreadablePlaylists: 0,
        startedAt: null, updatedAt: null, completedAt: null, lastInventoryAt: null,
        message: 'Reading'
      };

      expect(Present.isInventoryStatus(status)).toBe(true);
      expect(Present.isAnalysis(status)).toBe(false);
      expect(Present.inventoryProgress(status as any)).toBe(25);
    });

    it('recognizes a persisted discovery draft', () => {
      const draft = {
        id: 'draft', state: 'Ready', name: 'Deep South', summary: 'A sequence',
        userPrompts: ['1950s Deep South music'], desiredTrackCount: 25,
        clarifyingQuestion: null, candidates: [], knownMusicCoverage: 'Partial evidence',
        createdAt: new Date().toISOString(), updatedAt: new Date().toISOString()
      };

      expect(Present.isDiscoveryDraft(draft)).toBe(true);
      expect(Present.isInventoryStatus(draft)).toBe(false);
    });

    it('renders catalog resolution independently from familiarity evidence', () => {
      const candidate = (resolution: any) => ({
        id: 'candidate', position: 0, artist: 'Artist', title: 'Song', rationale: null,
        resolution, track: null, alternatives: [], probablyUnfamiliar: true,
        familiarityLabel: 'Probably unfamiliar'
      });

      expect(Present.candidateResolutionLabel(candidate('Resolved')))
        .toBe('Matched in Spotify catalog');
      expect(Present.candidateResolutionLabel(candidate('Ambiguous')))
        .toBe('Multiple Spotify catalog matches');
      expect(Present.candidateResolutionLabel(candidate('NotFound')))
        .toBe('No confident Spotify catalog match');
      expect(Present.candidateResolutionLabel(candidate(1)))
        .toBe('Multiple Spotify catalog matches');
    });
  });
});
