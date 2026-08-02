import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { SpotifinatorComponent } from './spotifinator.component';
import { SpotifyPlaylist, SpotifyPlaylistItem, SpotifyPlan, SpotifyPlanPreview } from './spotifinator.models';

describe('SpotifinatorComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SpotifinatorComponent, NoopAnimationsModule],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // The component reads route params; without this the standalone
        // injector has no ActivatedRoute at all.
        provideRouter([])
      ]
    }).compileComponents();
  });

  it('should create', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    expect(component).toBeTruthy();
  });

  it('should render the chat card', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const card = compiled.querySelector('.chat-card');
    expect(card).toBeTruthy();
  });

  it('should have a welcome message on init', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    expect(component.messages.length).toBe(1);
    expect(component.messages[0].role).toBe('assistant');
    expect(component.messages[0].content.split('\n')).toHaveSize(1);
  });

  it('should render the input area', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const inputArea = compiled.querySelector('.input-area');
    expect(inputArea).toBeTruthy();
  });

  it('should have idle viewState initially', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    expect(component.viewState).toBe('idle');
  });

  it('should not submit empty messages', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    component.userInput = '   ';
    component.onSubmit();
    // Should still only have the welcome message
    expect(component.messages.length).toBe(1);
  });

  it('should format duration correctly', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    expect(component.formatDuration(180000)).toBe('3:00');
    expect(component.formatDuration(65000)).toBe('1:05');
    expect(component.formatDuration(30000)).toBe('0:30');
  });

  // ─── "unknown is not zero" ─────────────────────────────────────────────────

  describe('itemCountLabel', () => {
    const playlist = (over: Partial<SpotifyPlaylist>): SpotifyPlaylist => ({
      id: 'p', name: 'P', imageUrl: null, trackCount: 0, spotifyUrl: null,
      contentsAvailable: true, snapshotId: null, ownerId: null, ownerName: null,
      isOwnedByUser: false, isCollaborative: false, isPublic: null, uri: null, inventoryAt: null,
      ...over
    });

    let component: SpotifinatorComponent;
    beforeEach(() => {
      component = TestBed.createComponent(SpotifinatorComponent).componentInstance;
    });

    it('shows a real count when Spotify reported one', () => {
      expect(component.itemCountLabel(playlist({ trackCount: 17 }))).toBe('17 items');
    });

    it('singularises a one-item playlist', () => {
      expect(component.itemCountLabel(playlist({ trackCount: 1 }))).toBe('1 item');
    });

    it('says empty playlists have 0 items', () => {
      expect(component.itemCountLabel(playlist({ trackCount: 0 }))).toBe('0 items');
    });

    it('never renders a number when contents are unavailable', () => {
      // The headline bug: a followed playlist full of music must not read as 0.
      const label = component.itemCountLabel(
        playlist({ trackCount: null, contentsAvailable: false }));

      expect(label).toBe('Contents unavailable');
      expect(label).not.toContain('0');
    });

    it('trusts contentsAvailable=false even if a count leaked through', () => {
      expect(component.itemCountLabel(playlist({ trackCount: 5, contentsAvailable: false })))
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

    let component: SpotifinatorComponent;
    beforeEach(() => {
      component = TestBed.createComponent(SpotifinatorComponent).componentInstance;
    });

    it('marks playlists you own', () => {
      expect(component.ownershipLabel(playlist({ isOwnedByUser: true }))).toBe('Yours');
    });

    it('marks collaborative playlists', () => {
      expect(component.ownershipLabel(playlist({ isCollaborative: true }))).toBe('Collaborative');
    });

    it('names the owner of a followed playlist', () => {
      expect(component.ownershipLabel(playlist({ ownerName: 'Mom' }))).toBe('Followed · Mom');
    });

    it('still says followed when the owner name is missing', () => {
      expect(component.ownershipLabel(playlist({}))).toBe('Followed');
    });
  });

  // ─── item rendering ────────────────────────────────────────────────────────

  describe('itemMeta', () => {
    const item = (over: Partial<SpotifyPlaylistItem>): SpotifyPlaylistItem => ({
      position: 0, kind: 'Track', id: 't', name: 'Song', uri: 'spotify:track:t',
      artists: 'Artist', albumName: 'Album', durationMs: 180000, spotifyUrl: null,
      isLocal: false, addedAt: null, isrc: null,
      ...over
    });

    let component: SpotifinatorComponent;
    beforeEach(() => {
      component = TestBed.createComponent(SpotifinatorComponent).componentInstance;
    });

    it('describes a track with artist, album and duration', () => {
      expect(component.itemMeta(item({}))).toBe('Artist · Album · 3:00');
    });

    it('omits the empty artist line for a podcast episode', () => {
      expect(component.itemMeta(item({ kind: 'Episode', artists: '', albumName: null })))
        .toBe('3:00');
    });

    it('flags a local file', () => {
      expect(component.itemMeta(item({ kind: 'Local' }))).toContain('local file');
    });

    it('explains an item that is no longer on Spotify', () => {
      expect(component.itemMeta(item({ kind: 'Unavailable', artists: '', albumName: null, durationMs: 0 })))
        .toBe('This item is no longer on Spotify');
    });
  });

  describe('type guards', () => {
    let component: SpotifinatorComponent;
    beforeEach(() => {
      component = TestBed.createComponent(SpotifinatorComponent).componentInstance;
    });

    it('tells an items page apart from a playlist', () => {
      const page = { playlistId: 'p', items: [], total: 0, offset: 0, limit: 50,
                     hasMore: false, access: 'Available', snapshotId: null };

      expect(component.isItemsPage(page)).toBe(true);
      expect(component.isPlaylist(page)).toBe(false);
    });

    it('tells a playlist apart from an items page', () => {
      const playlist = { id: 'p', name: 'P', contentsAvailable: true, trackCount: 3 };

      expect(component.isPlaylist(playlist)).toBe(true);
      expect(component.isItemsPage(playlist)).toBe(false);
    });

    it('does not mistake an empty array for results', () => {
      expect(component.isPlaylistArray([])).toBe(false);
      expect(component.isRecentContexts([])).toBe(false);
    });

    it('treats null data as nothing to render', () => {
      expect(component.isPlaylist(null)).toBe(false);
      expect(component.isItemsPage(null)).toBe(false);
      expect(component.isSearchResult(null)).toBe(false);
    });

    it('recognizes inventory progress without mistaking it for analysis', () => {
      const status = {
        jobId: 'job', state: 'Running', totalPlaylists: 100, processedPlaylists: 25,
        readablePlaylists: 24, partialPlaylists: 1, unreadablePlaylists: 0,
        startedAt: null, updatedAt: null, completedAt: null, lastInventoryAt: null,
        message: 'Reading'
      };

      expect(component.isInventoryStatus(status)).toBe(true);
      expect(component.isAnalysis(status)).toBe(false);
      expect(component.inventoryProgress(status as any)).toBe(25);
    });

    it('recognizes a persisted discovery draft', () => {
      const draft = {
        id: 'draft', state: 'Ready', name: 'Deep South', summary: 'A sequence',
        userPrompts: ['1950s Deep South music'], desiredTrackCount: 25,
        clarifyingQuestion: null, candidates: [], knownMusicCoverage: 'Partial evidence',
        createdAt: new Date().toISOString(), updatedAt: new Date().toISOString()
      };

      expect(component.isDiscoveryDraft(draft)).toBe(true);
      expect(component.isInventoryStatus(draft)).toBe(false);
    });

    it('renders catalog resolution independently from familiarity evidence', () => {
      const candidate = (resolution: any) => ({
        id: 'candidate', position: 0, artist: 'Artist', title: 'Song', rationale: null,
        resolution, track: null, alternatives: [], probablyUnfamiliar: true,
        familiarityLabel: 'Probably unfamiliar'
      });

      expect(component.candidateResolutionLabel(candidate('Resolved')))
        .toBe('Matched in Spotify catalog');
      expect(component.candidateResolutionLabel(candidate('Ambiguous')))
        .toBe('Multiple Spotify catalog matches');
      expect(component.candidateResolutionLabel(candidate('NotFound')))
        .toBe('No confident Spotify catalog match');
      expect(component.candidateResolutionLabel(candidate(1)))
        .toBe('Multiple Spotify catalog matches');
    });
  });

  it('replaces a queued chat card when the dedicated status endpoint advances', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    const queued = {
      jobId: 'job', state: 'Queued', totalPlaylists: 0, processedPlaylists: 0,
      readablePlaylists: 0, partialPlaylists: 0, unreadablePlaylists: 0,
      startedAt: null, updatedAt: null, completedAt: null, lastInventoryAt: null,
      message: 'Queued'
    };
    component.messages.push({
      id: 'status', role: 'assistant', content: 'Inventory queued',
      timestamp: new Date(), data: queued as any
    });

    component.loadInventoryStatus();
    const request = http.expectOne(req => req.url.endsWith('/api/spotify/inventory/status'));
    request.flush({
      ...queued, state: 'Partial', totalPlaylists: 258, processedPlaylists: 258,
      readablePlaylists: 177, unreadablePlaylists: 81, message: 'Inventory finished with limits'
    });

    expect(component.inventoryStatus?.state).toBe('Partial');
    expect((component.messages.at(-1)!.data as any).state).toBe('Partial');
    expect((component.messages.at(-1)!.data as any).processedPlaylists).toBe(258);
    http.verify();
  });

  it('saves a draft into the sidebar and can close the active workspace', () => {
    const component = TestBed.createComponent(SpotifinatorComponent).componentInstance;
    const http = TestBed.inject(HttpTestingController);
    const draft: any = {
      id: 'draft', state: 'Ready', name: 'Deep South', summary: 'A sequence',
      userPrompts: ['prompt'], desiredTrackCount: 25, clarifyingQuestion: null,
      candidates: [], knownMusicCoverage: 'coverage', createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(), savedAt: null
    };
    component.activeDraft = draft;

    component.saveActiveDraft();
    const save = http.expectOne(req => req.method === 'PATCH' && req.url.endsWith('/drafts/draft'));
    expect(save.request.body.saved).toBe(true);
    const saved = { ...draft, savedAt: new Date().toISOString() };
    save.flush(saved);
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/drafts')).flush([saved]);

    expect(component.savedDrafts).toHaveSize(1);
    component.closeActiveDraft();
    expect(component.activeDraft).toBeNull();
    expect(component.savedDrafts).toHaveSize(1);
    expect(localStorage.getItem('spotifinator.activeDraftId')).toBeNull();
    http.verify();
  });

  // ─── change plan gating ────────────────────────────────────────────────────

  describe('change plans', () => {
    const plan = (over: Partial<SpotifyPlan> = {}, preview: Partial<SpotifyPlanPreview> = {}): SpotifyPlan => ({
      id: 'plan-1', action: 'AddItems', safetyTier: 'Additive', status: 'AwaitingConfirmation',
      createdAtUtc: '2026-08-02T12:00:00Z', expiresAtUtc: '2026-08-02T12:30:00Z',
      targets: [], steps: [], originalRequest: null, confirmedBy: null, confirmedAtUtc: null,
      failure: null, canUndo: false, undoOfPlanId: null,
      preview: {
        summary: 'Add 3 tracks', confirmLabel: 'Add 3 tracks', effects: [], warnings: [],
        requiresHighImpactAcknowledgement: false, itemsAdded: 3, itemsRemoved: 0,
        itemsSkippedAsDuplicates: 0, itemsUnresolved: 0, playlistsAffected: 1,
        ...preview
      },
      ...over
    });

    let component: SpotifinatorComponent;
    beforeEach(() => {
      component = TestBed.createComponent(SpotifinatorComponent).componentInstance;
    });

    it('lets an ordinary additive plan be confirmed straight away', () => {
      expect(component.planIsBlocked(plan())).toBe(false);
    });

    it('blocks a high-impact plan until the box is ticked', () => {
      // The second gate on a destructive change. Without this a replace could be
      // confirmed by the same single click as an ordinary add.
      const destructive = plan({}, { requiresHighImpactAcknowledgement: true });

      expect(component.planIsBlocked(destructive)).toBe(true);

      component.toggleHighImpact(destructive, true);
      expect(component.planIsBlocked(destructive)).toBe(false);
    });

    it('re-blocks when the acknowledgement is unticked', () => {
      const destructive = plan({}, { requiresHighImpactAcknowledgement: true });

      component.toggleHighImpact(destructive, true);
      component.toggleHighImpact(destructive, false);

      expect(component.planIsBlocked(destructive)).toBe(true);
    });

    it('does not carry an acknowledgement across to a different plan', () => {
      const first = plan({ id: 'a' }, { requiresHighImpactAcknowledgement: true });
      const second = plan({ id: 'b' }, { requiresHighImpactAcknowledgement: true });

      component.toggleHighImpact(first, true);

      expect(component.planIsBlocked(second)).toBe(true);
    });

    it('only offers actions while the plan is still awaiting a decision', () => {
      expect(component.planIsPending(plan({ status: 'AwaitingConfirmation' }))).toBe(true);
      expect(component.planIsPending(plan({ status: 'Completed' }))).toBe(false);
      expect(component.planIsPending(plan({ status: 'Cancelled' }))).toBe(false);
      expect(component.planIsPending(plan({ status: 'Expired' }))).toBe(false);
    });

    it('explains an expired plan in terms of what happened', () => {
      expect(component.planStatusLabel(plan({ status: 'Expired' }))).toContain('playlist changed');
    });

    it('distinguishes partly done from failed', () => {
      expect(component.planStatusLabel(plan({ status: 'PartiallyCompleted' }))).toBe('Partly done');
      expect(component.planStatusLabel(plan({ status: 'Failed' }))).toBe('Failed');
    });

    it('recognises a plan in the transcript', () => {
      expect(component.isPlan(plan())).toBe(true);
      expect(component.isPlan({ tracks: [] })).toBe(false);
      expect(component.isPlan(null)).toBe(false);
    });
  });
});
