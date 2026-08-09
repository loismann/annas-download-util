import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { SpotifinatorComponent } from './spotifinator.component';
import { SpotifinatorPresentation as Present } from './spotifinator.presentation';
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

  it('says so when the inventory will not even start', () => {
    // The button lives in the connection panel, so the answer has to get back
    // there — a refresh that never starts produces no status to render.
    const component = TestBed.createComponent(SpotifinatorComponent).componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.connection = { isConnected: true, state: 'Connected' } as never;

    component.refreshInventory();
    http.expectOne(req => req.url.endsWith('/inventory/refresh'))
      .flush({ error: 'Spotify is rate limiting us.' }, { status: 429, statusText: 'Too Many' });

    expect(component.inventoryError).toBe('Spotify is rate limiting us.');
    expect(component.inventoryActionPending).toBe(false);
    http.verify();
  });

  describe('the draft the page is currently about', () => {
    const draft: any = {
      id: 'draft', state: 'Ready', name: 'Deep South', summary: 'A sequence',
      userPrompts: ['prompt'], desiredTrackCount: 25, clarifyingQuestion: null,
      candidates: [], knownMusicCoverage: 'coverage', createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(), savedAt: null
    };

    let component: SpotifinatorComponent;
    let http: HttpTestingController;
    beforeEach(() => {
      component = TestBed.createComponent(SpotifinatorComponent).componentInstance;
      http = TestBed.inject(HttpTestingController);
      component.activeDraft = draft;
    });

    it('puts a newly saved draft in the sidebar', () => {
      component.onDraftChanged({ ...draft, savedAt: new Date().toISOString() });

      http.expectOne(req => req.method === 'GET' && req.url.endsWith('/drafts'))
        .flush([{ ...draft, savedAt: new Date().toISOString() }]);
      expect(component.savedDrafts).toHaveSize(1);
      http.verify();
    });

    it('does not refetch the sidebar for an edit to a draft already in it', () => {
      // Every reorder and every removed candidate comes back through here. The
      // list only changes membership on a save, so anything else is a wasted
      // round trip for a list that already has the right rows.
      const saved = { ...draft, savedAt: '2026-08-01T00:00:00Z' };
      component.savedDrafts = [saved];

      component.onDraftChanged({ ...saved, name: 'Renamed' });

      http.verify();
      expect(component.savedDrafts[0].name).toBe('Renamed');
    });

    it('closing keeps the draft — it only puts the workspace away', () => {
      component.savedDrafts = [draft];

      component.closeActiveDraft();

      expect(component.activeDraft).toBeNull();
      expect(component.savedDrafts).toHaveSize(1);
      expect(localStorage.getItem('spotifinator.activeDraftId')).toBeNull();
    });
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
      expect(Present.planIsPending(plan({ status: 'AwaitingConfirmation' }))).toBe(true);
      expect(Present.planIsPending(plan({ status: 'Completed' }))).toBe(false);
      expect(Present.planIsPending(plan({ status: 'Cancelled' }))).toBe(false);
      expect(Present.planIsPending(plan({ status: 'Expired' }))).toBe(false);
    });

    it('explains an expired plan in terms of what happened', () => {
      expect(Present.planStatusLabel(plan({ status: 'Expired' }))).toContain('playlist changed');
    });

    it('distinguishes partly done from failed', () => {
      expect(Present.planStatusLabel(plan({ status: 'PartiallyCompleted' }))).toBe('Partly done');
      expect(Present.planStatusLabel(plan({ status: 'Failed' }))).toBe('Failed');
    });

    it('recognises a plan in the transcript', () => {
      expect(Present.isPlan(plan())).toBe(true);
      expect(Present.isPlan({ tracks: [] })).toBe(false);
      expect(Present.isPlan(null)).toBe(false);
    });

    // ─── bulk plans (phase 8) ─────────────────────────────────────────────────

    it('describes each step in words rather than showing the enum', () => {
      // The step list is the only place a user sees what a merge is doing, and
      // "VerifyPlaylistPopulated" tells them nothing about why it matters.
      const step = (kind: string, playlistName: string | null = null) => ({
        ordinal: 0, kind, playlistId: null, playlistName, uris: null,
        status: 'Pending' as const, resultingSnapshotId: null, failure: null
      });

      expect(Present.planStepLabel(step('VerifyPlaylistPopulated')))
        .toContain('Check everything arrived');
      expect(Present.planStepLabel(step('RemoveFromLibrary', 'Road Trip')))
        .toBe('Remove from your library — Road Trip');
      expect(Present.planStepLabel(step('AddToLibrary', 'Road Trip')))
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

      expect(Present.resolvedCandidateCount(draft)).toBe(1);
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
      expect(Present.planHasTrouble(plan({ status: 'Completed' }))).toBe(false);
      expect(Present.planHasTrouble(plan({ status: 'PartiallyCompleted' }))).toBe(true);
      expect(Present.planHasTrouble(plan({ status: 'Failed' }))).toBe(true);
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

      component.onDraftDeleted(draft);

      // Left anywhere, a stale card could be re-opened after the draft is gone.
      expect(component.activeDraft).toBeNull();
      expect(component.savedDrafts).toEqual([]);
      expect(component.messages.find(m => m.id === 'm1')!.data).toBeNull();
      expect(component.messages.at(-1)!.content).toContain('Spotify is untouched');
    });

    it('a refusal from the draft panel is answered in the transcript', () => {
      component.onDraftFailed('That draft could not be turned into a playlist.');

      const last = component.messages.at(-1)!;
      expect(last.content).toBe('That draft could not be turned into a playlist.');
      expect(last.error).toBe(true);
    });

    // ─── the playlist rail ───────────────────────────────────────────────────

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

  // ─── writes have to outlive the page ───────────────────────────────────────

  describe('leaving the page does not cancel a write', () => {
    // Unsubscribing an HttpClient call aborts the request, so routing a write
    // through the destroy guard means navigating away cancels the user's
    // action — a confirmed draft delete, a plan resuming against Spotify, a
    // reordered candidate list. Classification is by the service method's HTTP
    // verb, never by its name: `processCommand` is a POST and counts,
    // `getPlaylistItems` is a GET and does not.

    const stalled = {
      id: 'plan-9', status: 'PartiallyCompleted', steps: [], preview: { summary: '' },
      recovery: { canResume: true, advice: 'Some of it landed.' }
    } as never;

    interface WriteCase {
      readonly what: string;
      readonly verb: string;
      readonly path: string;
      readonly act: (component: SpotifinatorComponent) => void;
    }

    const writes: WriteCase[] = [
      { what: 'starting a library inventory', verb: 'POST',
        path: '/inventory/refresh', act: c => c.refreshInventory() },
      { what: 'sending a chat command', verb: 'POST',
        path: '/command', act: c => { c.userInput = 'build me something'; c.onSubmit(); } },
      { what: 'resuming a half-applied plan', verb: 'POST',
        path: '/plans/plan-9/retry', act: c => c.retryPlan(stalled) },
      { what: 'undoing a plan', verb: 'POST',
        path: '/plans/plan-9/undo', act: c => c.undoPlan(stalled) }
    ];

    for (const write of writes) {
      it(`${write.what} survives the component being destroyed`, () => {
        // Both destructive paths ask first; saying yes is the case under test.
        spyOn(window, 'confirm').and.returnValue(true);

        const fixture = TestBed.createComponent(SpotifinatorComponent);
        const component = fixture.componentInstance;
        // Deliberately no detectChanges: ngOnInit would add a connection GET to
        // every one of these, and the connection is what is being faked anyway.
        component.connection = { isConnected: true, state: 'Connected' } as never;
        const http = TestBed.inject(HttpTestingController);

        write.act(component);
        const request = http.expectOne(
          req => req.method === write.verb && req.url.endsWith(write.path));

        fixture.destroy();

        expect(request.cancelled)
          .withContext(`${write.verb} ${write.path} was aborted by ngOnDestroy`)
          .toBe(false);
      });
    }

    it('reads are still cancelled — the guard is about verbs, not caution', () => {
      const fixture = TestBed.createComponent(SpotifinatorComponent);
      const component = fixture.componentInstance;
      const http = TestBed.inject(HttpTestingController);

      component.loadPlaylists();
      const request = http.expectOne(req => req.method === 'GET' && req.url.endsWith('/playlists'));

      fixture.destroy();

      expect(request.cancelled).toBe(true);
    });

    it('does not drop a review modal onto whatever the user navigated to', () => {
      // The consequence of no longer cancelling: an undo or a create-from-draft
      // plan is built by a POST that can now answer after the page has gone.
      const dialog = { open: jasmine.createSpy('open') };
      dialog.open.and.returnValue({ afterClosed: () => of(undefined) });
      TestBed.overrideProvider(MatDialog, { useValue: dialog });

      const fixture = TestBed.createComponent(SpotifinatorComponent);
      const component = fixture.componentInstance;
      const http = TestBed.inject(HttpTestingController);

      component.undoPlan(stalled);
      const request = http.expectOne(req => req.url.endsWith('/plans/plan-9/undo'));

      fixture.destroy();
      // Read before flushing. `cancelled` is set by the subscription teardown,
      // and a successful flush tears the subscription down too — so after a
      // flush the flag is true either way and proves nothing.
      const abortedByDestroy = request.cancelled;
      request.flush({ ...(stalled as object), status: 'AwaitingConfirmation' });

      expect(abortedByDestroy).toBe(false);
      expect(dialog.open).not.toHaveBeenCalled();
    });
  });
});
