import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { SpotifyDraftPanelComponent } from './draft-panel.component';
import { SpotifyDiscoveryDraft } from '../spotifinator.models';

describe('SpotifyDraftPanelComponent', () => {
  let fixture: ComponentFixture<SpotifyDraftPanelComponent>;
  let component: SpotifyDraftPanelComponent;
  let http: HttpTestingController;

  const candidate = (over: Record<string, unknown> = {}) => ({
    id: 'c1', position: 0, title: 'One', artist: 'A', resolution: 'Resolved',
    track: { id: 't1', spotifyUrl: null }, alternatives: [], rationale: null,
    familiarityLabel: '', probablyUnfamiliar: false, ...over
  });

  const draft = (over: Record<string, unknown> = {}): SpotifyDiscoveryDraft => ({
    id: 'd1', name: 'Morr', state: 'Ready', summary: 'A sequence',
    clarifyingQuestion: null, knownMusicCoverage: '', desiredTrackCount: 25,
    userPrompts: ['morr music'], savedAt: null,
    createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z',
    candidates: [
      candidate(),
      candidate({ id: 'c2', position: 1, title: 'Two', artist: 'B', track: { id: 't2' } })
    ],
    ...over
  } as unknown as SpotifyDiscoveryDraft);

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SpotifyDraftPanelComponent, NoopAnimationsModule],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    }).compileComponents();

    fixture = TestBed.createComponent(SpotifyDraftPanelComponent);
    component = fixture.componentInstance;
    component.draft = draft();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  // ─── editing the candidate list ──────────────────────────────────────────

  it('hands the updated draft back rather than keeping its own copy', () => {
    // The same draft is also a card in the transcript and a row in the sidebar.
    // Mutating the input here would leave those two showing the old version.
    const changed = jasmine.createSpy('changed');
    component.changed.subscribe(changed);

    component.removeCandidate('c2');
    const request = http.expectOne(req => req.method === 'PATCH' && req.url.endsWith('/drafts/d1'));
    expect(request.request.body.removeCandidateIds).toEqual(['c2']);
    const updated = draft({ candidates: [candidate()] });
    request.flush(updated);

    expect(changed).toHaveBeenCalledWith(updated);
  });

  it('sends the whole new order, not the one candidate that moved', () => {
    // The endpoint replaces the ordering wholesale; sending just the moved id
    // would leave every other position to be guessed at.
    component.moveCandidate('c2', -1);

    const request = http.expectOne(req => req.method === 'PATCH' && req.url.endsWith('/drafts/d1'));
    expect(request.request.body.orderedCandidateIds).toEqual(['c2', 'c1']);
    request.flush(draft());
  });

  it('refuses to move the first candidate up or the last one down', () => {
    // The buttons are disabled at the ends, but a keyboard repeat can still land
    // here — and a negative index would silently reorder into `undefined`.
    component.moveCandidate('c1', -1);
    component.moveCandidate('c2', 1);

    http.expectNone(() => true);
  });

  it('ignores a move for a candidate that is no longer in the draft', () => {
    component.moveCandidate('gone', 1);

    http.expectNone(() => true);
  });

  it('records which Spotify track an ambiguous candidate meant', () => {
    component.selectAlternative('c1', 'track-42');

    const request = http.expectOne(req => req.method === 'PATCH' && req.url.endsWith('/drafts/d1'));
    expect(request.request.body.candidateSelections).toEqual({ c1: 'track-42' });
    request.flush(draft());
  });

  // ─── creating and deleting ───────────────────────────────────────────────

  it('will not offer to create a draft where nothing matched', () => {
    component.draft = draft({ candidates: [candidate({ resolution: 'NotFound', track: null })] });

    component.createInSpotify();

    http.expectNone(() => true);
  });

  it('creating builds a plan for review — it does not write to Spotify', () => {
    // The button must not be a shortcut past the confirmation. It asks the server
    // for a plan and hands it up; something else decides whether to execute it.
    const planBuilt = jasmine.createSpy('planBuilt');
    component.planBuilt.subscribe(planBuilt);

    component.createInSpotify();

    const request = http.expectOne(req => req.method === 'POST' && req.url.endsWith('/plans'));
    expect(request.request.body.action).toBe('CreatePlaylist');
    const built = { id: 'plan-1', status: 'AwaitingConfirmation' };
    request.flush(built);

    expect(planBuilt).toHaveBeenCalledWith(built as never);
  });

  it('says why a draft was refused instead of failing silently', () => {
    // A refusal is a 400 carrying the real sentence — nothing resolved, no name,
    // over the ceiling. It is an answer, not a crash.
    const failed = jasmine.createSpy('failed');
    component.failed.subscribe(failed);

    component.createInSpotify();
    http.expectOne(req => req.url.endsWith('/plans'))
      .flush({ error: 'That playlist name is already taken.' },
             { status: 400, statusText: 'Bad Request' });

    expect(failed).toHaveBeenCalledWith('That playlist name is already taken.');
    expect(component.pending).toBe(false);
  });

  it('asks before deleting a draft, and does nothing if you say no', () => {
    spyOn(window, 'confirm').and.returnValue(false);

    component.delete();

    http.expectNone(() => true);
  });

  it('says how much is being thrown away before it asks', () => {
    // "Delete?" alone does not say that two matched tracks go with it, nor that
    // Spotify is untouched — which is the thing that makes this safe to confirm.
    const confirmSpy = spyOn(window, 'confirm').and.returnValue(false);

    component.delete();

    const asked = confirmSpy.calls.mostRecent().args[0] as string;
    expect(asked).toContain('2 candidates (2 matched)');
    expect(asked).toContain('Nothing on Spotify is affected');
  });

  it('reports the draft it deleted so the rest of the page can drop it', () => {
    spyOn(window, 'confirm').and.returnValue(true);
    const deleted = jasmine.createSpy('deleted');
    const gone = component.draft;
    component.deleted.subscribe(deleted);

    component.delete();
    http.expectOne(req => req.method === 'DELETE' && req.url.endsWith('/drafts/d1'))
      .flush(null);

    expect(deleted).toHaveBeenCalledWith(gone);
  });

  // ─── one action at a time ────────────────────────────────────────────────

  it('will not let Create race Delete', () => {
    // Both are pending-guarded through the same flag on purpose: creating a
    // playlist from a draft that is being deleted has no defined outcome.
    spyOn(window, 'confirm').and.returnValue(true);

    component.createInSpotify();
    component.delete();

    http.expectOne(req => req.url.endsWith('/plans')).flush({ id: 'p' });
  });

  it('releases the guard when a save fails so it can be tried again', () => {
    component.save();
    http.expectOne(req => req.method === 'PATCH' && req.url.endsWith('/drafts/d1'))
      .flush({}, { status: 500, statusText: 'Server Error' });

    expect(component.pending).toBe(false);

    component.save();
    http.expectOne(req => req.method === 'PATCH' && req.url.endsWith('/drafts/d1'))
      .flush(draft({ savedAt: '2026-08-01T00:00:00Z' }));
  });

  // ─── writes have to outlive the panel ────────────────────────────────────

  it('does not cancel a write when the panel goes away', () => {
    // Unsubscribing an HttpClient call aborts the request. Every write here is a
    // PATCH, POST or DELETE the user asked for, so none of them may be tied to
    // how long this component happens to stay on screen.
    spyOn(window, 'confirm').and.returnValue(true);

    component.delete();
    const request = http.expectOne(req => req.method === 'DELETE' && req.url.endsWith('/drafts/d1'));

    fixture.destroy();

    expect(request.cancelled).toBe(false);
    request.flush(null);
  });
});
