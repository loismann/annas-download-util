import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { SpotifyConnectionPanelComponent } from './connection-panel.component';
import { SpotifyConnectionStatus } from '../spotifinator.models';

describe('SpotifyConnectionPanelComponent', () => {
  let fixture: ComponentFixture<SpotifyConnectionPanelComponent>;
  let component: SpotifyConnectionPanelComponent;
  let http: HttpTestingController;

  const status = (over: Partial<SpotifyConnectionStatus> = {}): SpotifyConnectionStatus => ({
    isConnected: true, state: 'Connected', displayName: 'tamupino', accountId: 'acct',
    grantedScopes: ['streaming'], missingScopes: [], warning: null, lastError: null,
    lastSuccessfulCallAt: '2026-08-01T00:00:00Z', daysUntilReauthorization: 30,
    rateLimitedUntil: null,
    ...over
  } as SpotifyConnectionStatus);

  /** Builds the panel with a given `?spotify=` return value from Spotify. */
  const build = async (oauthResult: string | null = null) => {
    // Reset first: the query param is a provider, so a test that wants a
    // different one has to reconfigure a module that is not yet instantiated.
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [SpotifyConnectionPanelComponent, NoopAnimationsModule],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              queryParamMap: convertToParamMap(oauthResult ? { spotify: oauthResult } : {})
            }
          }
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SpotifyConnectionPanelComponent);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
  };

  const connectionRequest = () =>
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/connection'));

  beforeEach(async () => await build());

  // ─── returning from Spotify ──────────────────────────────────────────────

  it('reports a successful authorization the user has no other confirmation of', async () => {
    await build('connected');
    fixture.detectChanges();
    connectionRequest().flush(status());
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.connection-notice').textContent)
      .toContain('connected successfully');
  });

  it('names the reason authorization did not complete rather than going silent', async () => {
    // Spotify puts the outcome in the query string; there is no response to read,
    // so a failure with no notice looks exactly like never having pressed connect.
    await build('access_denied');
    fixture.detectChanges();
    connectionRequest().flush(status({ isConnected: false, state: 'Disconnected' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.connection-notice').textContent)
      .toContain('access_denied');
  });

  // ─── the foldout ─────────────────────────────────────────────────────────

  it('keeps the panel shut when everything is healthy', () => {
    fixture.detectChanges();
    connectionRequest().flush(status());

    expect(component.needsAttention()).toBe(false);
  });

  it('opens whenever something needs doing', () => {
    fixture.detectChanges();
    connectionRequest().flush(status({ isConnected: false }));
    expect(component.needsAttention()).toBe(true);

    // The case that matters after adding playback scopes: connected, but the new
    // permissions have not been granted yet.
    component.connection = status({ missingScopes: ['streaming'] });
    expect(component.needsAttention()).toBe(true);

    component.connection = status({ warning: 'Spotify is rate limiting us.' });
    expect(component.needsAttention()).toBe(true);
  });

  it('opens while it still does not know, rather than looking healthy', () => {
    // Collapsed-and-fine is the wrong default for "no answer yet".
    expect(component.loading).toBe(true);
    expect(component.needsAttention()).toBe(true);

    fixture.detectChanges();
    connectionRequest().flush(status());
  });

  it('summarises the library in the one line the collapsed panel gets', () => {
    fixture.detectChanges();
    connectionRequest().flush(status());

    expect(component.summaryLabel()).toBe('Connected · inventory not refreshed yet');

    component.inventoryStatus = { totalPlaylists: 258, unreadablePlaylists: 0 } as never;
    expect(component.summaryLabel()).toBe('Connected · 258 playlists');

    // The unreadable count is the thing worth surfacing without opening anything.
    component.inventoryStatus = { totalPlaylists: 258, unreadablePlaylists: 81 } as never;
    expect(component.summaryLabel()).toBe('Connected · 258 playlists, 81 unreadable');
  });

  // ─── connecting and disconnecting ────────────────────────────────────────

  it('tells the page about the connection so the rest of it can start', () => {
    const changed = jasmine.createSpy('connectionChanged');
    component.connectionChanged.subscribe(changed);
    fixture.detectChanges();

    const connected = status();
    connectionRequest().flush(connected);

    expect(changed).toHaveBeenCalledWith(connected);
  });

  it('says so when the connection cannot be read at all', () => {
    fixture.detectChanges();
    connectionRequest().flush({ error: 'Spotify is unreachable.' },
      { status: 503, statusText: 'Service Unavailable' });

    expect(component.loading).toBe(false);
    expect(component.notice).toBe('Spotify is unreachable.');
  });

  it('asks before disconnecting, and does nothing if you say no', () => {
    spyOn(window, 'confirm').and.returnValue(false);
    fixture.detectChanges();
    connectionRequest().flush(status());

    component.disconnect();

    http.expectNone(req => req.method === 'DELETE');
  });

  it('announces a disconnect so account-bound state goes with it', () => {
    // Saved drafts belong to the account. Left behind they would reappear
    // against whoever connects next.
    spyOn(window, 'confirm').and.returnValue(true);
    const disconnected = jasmine.createSpy('disconnected');
    component.disconnected.subscribe(disconnected);
    fixture.detectChanges();
    connectionRequest().flush(status());

    component.disconnect();
    http.expectOne(req => req.method === 'DELETE' && req.url.endsWith('/connection')).flush(null);

    expect(disconnected).toHaveBeenCalled();
    // And it re-reads, so the panel shows Disconnected rather than the old state.
    connectionRequest().flush(status({ isConnected: false, state: 'Disconnected' }));
    expect(component.connection!.isConnected).toBe(false);
  });

  // ─── writes have to outlive the panel ────────────────────────────────────

  it('does not cancel the disconnect when the panel goes away', () => {
    spyOn(window, 'confirm').and.returnValue(true);
    fixture.detectChanges();
    connectionRequest().flush(status());

    component.disconnect();
    const request = http.expectOne(req => req.method === 'DELETE');

    fixture.destroy();

    expect(request.cancelled).toBe(false);
  });

  it('does not send someone who has left the page off to Spotify', () => {
    // The POST reserves PKCE state and must complete, but the redirect it exists
    // to produce would land on whatever the user navigated to instead.
    // window.location.assign is non-configurable, so the component keeps a seam.
    const assign = spyOn(component as never as { navigateTo(url: string): void }, 'navigateTo');
    fixture.detectChanges();
    connectionRequest().flush(status({ isConnected: false }));

    component.connect();
    const request = http.expectOne(req => req.url.endsWith('/connection/authorize'));

    fixture.destroy();
    const abortedByDestroy = request.cancelled;
    request.flush({ authorizationUrl: 'https://accounts.spotify.com/authorize?x=1' });

    expect(abortedByDestroy).toBe(false);
    expect(assign).not.toHaveBeenCalled();
  });

  it('sends the browser to Spotify when the page is still there', () => {
    // window.location.assign is non-configurable, so the component keeps a seam.
    const assign = spyOn(component as never as { navigateTo(url: string): void }, 'navigateTo');
    fixture.detectChanges();
    connectionRequest().flush(status({ isConnected: false }));

    component.connect();
    http.expectOne(req => req.url.endsWith('/connection/authorize'))
      .flush({ authorizationUrl: 'https://accounts.spotify.com/authorize?x=1' });

    expect(assign).toHaveBeenCalledWith('https://accounts.spotify.com/authorize?x=1');
  });
});
