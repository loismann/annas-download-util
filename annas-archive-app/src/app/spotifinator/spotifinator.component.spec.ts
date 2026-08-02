import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { SpotifinatorComponent } from './spotifinator.component';
import { Subject, of } from 'rxjs';
import { SpotifinatorApiService } from '../services/spotifinator-api.service';
import { SpotifyPlaybackService } from '../services/spotify-playback.service';
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
      isLocal: false, addedAt: null, isrc: null, albumArtUrl: null,
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
      failure: null, canUndo: false, undoOfPlanId: null, recovery: null,
      preview: {
        summary: 'Add 3 tracks', confirmLabel: 'Add 3 tracks', effects: [], warnings: [],
        requiresHighImpactAcknowledgement: false, itemsAdded: 3, itemsRemoved: 0,
        itemsSkippedAsDuplicates: 0, itemsUnresolved: 0, playlistsAffected: 1,
        ...preview
      },
      ...over
    });

    let component: SpotifinatorComponent;
    // Overridden rather than spied on: MatDialogModule re-provides MatDialog, so a
    // spy installed on TestBed.inject(MatDialog) is not necessarily the instance the
    // component received.
    let dialog: { open: jasmine.Spy };

    beforeEach(() => {
      dialog = { open: jasmine.createSpy('open') };
      dialog.open.and.returnValue({ afterClosed: () => of(undefined) });
      TestBed.overrideProvider(MatDialog, { useValue: dialog });
      component = TestBed.createComponent(SpotifinatorComponent).componentInstance;
    });

    /** What the review modal hands back when it closes. */
    const closesWith = (result: SpotifyPlan | undefined) =>
      dialog.open.and.returnValue({ afterClosed: () => of(result) });

    // The high-impact gate itself moved to PlanReviewDialogComponent and is tested
    // there. What stays here is everything about how a *finished* plan is reported.

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

    // ─── bulk plans (phase 8) ─────────────────────────────────────────────────

    it('describes each step in words rather than showing the enum', () => {
      // The step list is the only place a user sees what a merge is doing, and
      // "VerifyPlaylistPopulated" tells them nothing about why it matters.
      const step = (kind: string, playlistName: string | null = null) => ({
        ordinal: 0, kind, playlistId: null, playlistName, uris: null,
        status: 'Pending' as const, resultingSnapshotId: null, failure: null
      });

      expect(component.planStepLabel(step('VerifyPlaylistPopulated')))
        .toContain('Check everything arrived');
      expect(component.planStepLabel(step('RemoveFromLibrary', 'Road Trip')))
        .toBe('Remove from your library — Road Trip');
      expect(component.planStepLabel(step('AddToLibrary', 'Road Trip')))
        .toBe('Put back in your library — Road Trip');
    });

    it('offers to finish the rest only when something is left to finish', () => {
      const stalled = plan({
        status: 'PartiallyCompleted',
        recovery: {
          canResume: true, stepsSucceeded: 3, stepsFailed: 1, stepsNotAttempted: 1,
          advice: '3 step(s) landed and 2 did not.'
        }
      });

      expect(stalled.recovery?.canResume).toBe(true);
      expect(plan({ status: 'Completed' }).recovery).toBeNull();
    });

    it('does nothing when asked to resume a plan that has nothing to resume', () => {
      // Guards the button being clicked on a completed plan — re-running work that
      // already landed is the one thing resume must never do.
      const api = TestBed.inject(SpotifinatorApiService);
      const retry = spyOn(api, 'retryPlan');

      component.retryPlan(plan({ status: 'Completed' }));

      expect(retry).not.toHaveBeenCalled();
    });

    // ─── draft actions ────────────────────────────────────────────────────────

    it('counts only candidates that matched a real Spotify track', () => {
      // The create button is labelled with this number and disabled at zero, so an
      // unmatched candidate must not be counted as creatable.
      const draft = {
        id: 'd1', name: 'Morr Music Essentials', candidates: [
          { id: 'c1', resolution: 'Resolved', track: { id: 't1' } },
          { id: 'c2', resolution: 'NotFound', track: null },
          { id: 'c3', resolution: 'Ambiguous', track: null }
        ]
      } as never;

      expect(component.resolvedCandidateCount(draft)).toBe(1);
    });

    it('will not offer to create a draft where nothing matched', () => {
      const api = TestBed.inject(SpotifinatorApiService);
      const build = spyOn(api, 'buildCreateFromDraftPlan');
      component.activeDraft = {
        id: 'd1', name: 'Empty', candidates: [{ id: 'c1', resolution: 'NotFound', track: null }]
      } as never;

      component.createDraftInSpotify();

      expect(build).not.toHaveBeenCalled();
    });

    it('creating from a draft opens the review modal rather than writing', () => {
      // Two properties in one, and both were broken before. The button must not be
      // a shortcut past the confirmation — and the confirmation must appear *here*,
      // not as a card in the chat pane the user is not looking at, which is what
      // made the button seem to do nothing at all.
      const api = TestBed.inject(SpotifinatorApiService);
      const confirmPlan = spyOn(api, 'confirmPlan');
      const built = plan({ action: 'CreatePlaylist', status: 'AwaitingConfirmation' });
      spyOn(api, 'buildCreateFromDraftPlan').and.returnValue(of(built));

      component.activeDraft = {
        id: 'd1', name: 'Morr', candidates: [{ id: 'c1', resolution: 'Resolved', track: { id: 't' } }]
      } as never;

      component.createDraftInSpotify();

      expect(dialog.open).toHaveBeenCalled();
      expect(dialog.open.calls.mostRecent().args[1].data.plan).toBe(built);
      expect(confirmPlan).not.toHaveBeenCalled();
    });

    it('leaves nothing in the transcript when a review is abandoned', () => {
      // Cancelling changed nothing, so a line saying so would be noise about a
      // non-event — exactly the clutter the modal was meant to remove.
      closesWith(undefined);
      const before = component.messages.length;

      component.reviewPlan(plan());

      expect(component.messages.length).toBe(before);
    });

    it('reports a completed change in one line and refreshes the catalog', () => {
      // The catalog is cached for fifteen minutes server-side, so without the
      // forced refresh a playlist you just created stays invisible — which reads
      // as the change not having worked.
      const api = TestBed.inject(SpotifinatorApiService);
      const getPlaylists = spyOn(api, 'getPlaylists').and.returnValue(of([]));
      closesWith(plan({ status: 'Completed' }));

      component.reviewPlan(plan());

      expect(getPlaylists).toHaveBeenCalledWith(true);
      const last = component.messages[component.messages.length - 1];
      expect(last.content).toBe('Add 3 tracks');
    });

    it('does not refresh the catalog when nothing was applied', () => {
      const api = TestBed.inject(SpotifinatorApiService);
      const getPlaylists = spyOn(api, 'getPlaylists').and.returnValue(of([]));
      closesWith(plan({ status: 'Failed', failure: 'Spotify said no.' }));

      component.reviewPlan(plan());

      expect(getPlaylists).not.toHaveBeenCalled();
    });

    it('offers the step list only when something went wrong', () => {
      // A plan that simply worked has nothing to explain, and an expander nobody
      // needs is the clutter this whole change is about.
      expect(component.planHasTrouble(plan({ status: 'Completed' }))).toBe(false);
      expect(component.planHasTrouble(plan({ status: 'PartiallyCompleted' }))).toBe(true);
      expect(component.planHasTrouble(plan({ status: 'Failed' }))).toBe(true);
    });

    it('asks before deleting a draft, and does nothing if you say no', () => {
      const api = TestBed.inject(SpotifinatorApiService);
      const del = spyOn(api, 'deleteDiscoveryDraft');
      spyOn(window, 'confirm').and.returnValue(false);
      component.activeDraft = { id: 'd1', name: 'Morr', candidates: [] } as never;

      component.deleteActiveDraft();

      expect(del).not.toHaveBeenCalled();
      expect(component.activeDraft).not.toBeNull();
    });

    it('clears the draft everywhere once it is deleted', () => {
      const api = TestBed.inject(SpotifinatorApiService);
      spyOn(api, 'deleteDiscoveryDraft').and.returnValue(of(void 0));
      spyOn(window, 'confirm').and.returnValue(true);

      // Must be a full draft shape: the transcript sweep uses isDiscoveryDraft,
      // which checks for candidates + desiredTrackCount + userPrompts.
      const draft = {
        id: 'd1', name: 'Morr', candidates: [], savedAt: '2026-08-01T00:00:00Z',
        desiredTrackCount: 25, userPrompts: ['morr music'], state: 'Ready',
        summary: '', clarifyingQuestion: null, knownMusicCoverage: '',
        createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z'
      } as never;
      component.activeDraft = draft;
      component.savedDrafts = [draft];
      component.messages.push({
        id: 'm1', role: 'assistant', content: 'here', timestamp: new Date(), data: draft
      });

      component.deleteActiveDraft();

      // Left anywhere, a stale card could be re-opened after the draft is gone.
      expect(component.activeDraft).toBeNull();
      expect(component.savedDrafts).toEqual([]);
      expect(component.messages.find(m => m.id === 'm1')!.data).toBeNull();
    });

    // ─── connection foldout ───────────────────────────────────────────────────

    it('keeps the connection panel shut when everything is healthy', () => {
      component.connectionLoading = false;
      component.connection = {
        isConnected: true, missingScopes: [], warning: null, lastError: null, displayName: 'tamupino'
      } as never;

      expect(component.connectionNeedsAttention()).toBe(false);
    });

    it('opens the connection panel whenever something needs doing', () => {
      component.connectionLoading = false;

      component.connection = { isConnected: false, missingScopes: [], warning: null, lastError: null } as never;
      expect(component.connectionNeedsAttention()).toBe(true);

      // The case that matters after adding playback scopes: connected, but the new
      // permissions have not been granted yet.
      component.connection = {
        isConnected: true, missingScopes: ['streaming'], warning: null, lastError: null
      } as never;
      expect(component.connectionNeedsAttention()).toBe(true);
    });

    // ─── library pane and playback ────────────────────────────────────────────

    const listItem = (over: Partial<SpotifyPlaylistItem> = {}): SpotifyPlaylistItem => ({
      position: 0, kind: 'Track', id: 't', name: 'Mystery Train',
      uri: 'spotify:track:t', artists: 'Elvis', albumName: 'Sun', durationMs: 146000,
      spotifyUrl: null, isLocal: false, addedAt: null, isrc: null, albumArtUrl: null,
      ...over
    });

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
      const playback = TestBed.inject(SpotifyPlaybackService);
      const play = spyOn(playback, 'play').and.resolveTo();
      component.playbackMode = 'local';
      component.selectedPlaylist = { id: 'p1', uri: 'spotify:playlist:p1' } as never;

      component.playItem(listItem({ position: 3 }));

      expect(play).toHaveBeenCalledWith({
        contextUri: 'spotify:playlist:p1', offsetPosition: 3
      });
    });

    it('does not lose the playlist you are looking at to a slow response', () => {
      // Click A, click B, then A's page arrives. Without the guard it overwrites B.
      const api = TestBed.inject(SpotifinatorApiService);
      const slow = new Subject<never>();
      spyOn(api, 'getPlaylistItems').and.returnValue(slow as never);

      component.openPlaylist({ id: 'a', name: 'A' } as never);
      component.openPlaylist({ id: 'b', name: 'B' } as never);

      slow.next({ playlistId: 'a', items: [listItem()], total: 1, offset: 0,
                  limit: 50, hasMore: false, access: 'Available', snapshotId: null } as never);

      expect(component.selectedPlaylist!.id).toBe('b');
      expect(component.selectedItems).toEqual([]);
    });

    it('sorts your own playlists above ones you merely follow', () => {
      const api = TestBed.inject(SpotifinatorApiService);
      spyOn(api, 'getPlaylists').and.returnValue(of([
        { id: '1', name: 'Zed', isOwnedByUser: false, isCollaborative: false },
        { id: '2', name: 'Alpha', isOwnedByUser: false, isCollaborative: true },
        { id: '3', name: 'Mine', isOwnedByUser: true, isCollaborative: false }
      ] as unknown as SpotifyPlaylist[]));

      component.loadPlaylists();

      expect(component.playlists.map(p => p.name)).toEqual(['Mine', 'Alpha', 'Zed']);
    });

    it('reports progress as a percentage of the track, guarding zero length', () => {
      component.playback = {
        isPlaying: true, progressMs: 73000, device: null,
        track: { durationMs: 146000 } as never
      } as never;
      expect(component.playbackProgressPercent()).toBe(50);

      component.playback = {
        isPlaying: true, progressMs: 10, device: null, track: { durationMs: 0 } as never
      } as never;
      expect(component.playbackProgressPercent()).toBe(0);
    });

    it('sends exactly one resume even if the button is hit twice', () => {
      const api = TestBed.inject(SpotifinatorApiService);
      const retry = spyOn(api, 'retryPlan').and.returnValue(new Subject<SpotifyPlan>());
      const stalled = plan({
        status: 'PartiallyCompleted',
        recovery: {
          canResume: true, stepsSucceeded: 1, stepsFailed: 1, stepsNotAttempted: 0,
          advice: 'Some of it landed.'
        }
      });

      component.retryPlan(stalled);
      component.retryPlan(stalled);

      expect(retry).toHaveBeenCalledTimes(1);
    });
  });
});
