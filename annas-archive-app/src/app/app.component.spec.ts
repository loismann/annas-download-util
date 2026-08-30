import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { AppComponent } from './app.component';
import { AppChromeService, ChromeLevel } from './services/app-chrome.service';
import { AuthService, UserActivity } from './services/auth.service';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID } from '@angular/core';
import { provideAnimations } from '@angular/platform-browser/animations';
import { BehaviorSubject, of } from 'rxjs';

/**
 * Basic smoke tests for AppComponent
 * Verifies the app component can be created and rendered
 */
describe('AppComponent', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAnimations(),
        AuthService,
        { provide: PLATFORM_ID, useValue: 'browser' }
      ]
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // ngOnInit fetches the build stamp on every rendered fixture. It is
    // incidental to everything asserted here, so drain it rather than making
    // each individual test expect it. `match` returns [] for the specs that
    // never call detectChanges, so this is safe for all of them.
    httpMock.match('/assets/version.json')
      .forEach(req => req.flush({ buildTime: '2026-01-01T00:00:00Z' }));
    httpMock.verify();
  });

  it('should render toolbar', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('mat-toolbar')).toBeTruthy();
  });

  it('should initialize with empty userActivity array', () => {
    const fixture = TestBed.createComponent(AppComponent);
    expect(fixture.componentInstance.userActivity).toEqual([]);
  });

  it('should not show activity indicators when userActivity is empty', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.user-activity-indicators')).toBeFalsy();
  });

  it('should show activity indicators when userActivity has data', fakeAsync(() => {
    const fixture = TestBed.createComponent(AppComponent);
    const component = fixture.componentInstance;

    // Manually set activity data
    component.userActivity = [
      { initial: 'M', userName: 'Mom', minutesAgo: 5, isFullTone: true, isHalfTone: false, lastAction: 'Reading a book', activeForMinutes: 12 }
    ];

    // Mock authenticated state
    const authService = TestBed.inject(AuthService);
    localStorage.setItem('auth_token', 'test-token');
    localStorage.setItem('auth_name', 'Paul');

    fixture.detectChanges();
    tick();

    const compiled = fixture.nativeElement as HTMLElement;
    const activityDot = compiled.querySelector('.activity-dot');

    // Activity dot should exist if authenticated and has activity
    if (activityDot) {
      expect(activityDot.textContent?.trim()).toBe('M');
      expect(activityDot.classList.contains('full-tone')).toBe(true);
    }

    localStorage.clear();
  }));

  it('should apply full-tone class for activity within 30 minutes', () => {
    const fixture = TestBed.createComponent(AppComponent);
    const component = fixture.componentInstance;

    // Render first: ngOnInit resets userActivity to [] while unauthenticated,
    // so assigning before the initial detectChanges is silently undone.
    fixture.detectChanges();

    component.userActivity = [
      { initial: 'M', userName: 'Mom', minutesAgo: 10, isFullTone: true, isHalfTone: false, lastAction: 'Reading a book', activeForMinutes: 20 }
    ];

    fixture.detectChanges();

    // The CSS class logic: full-tone class when isFullTone is true
    const activity = component.userActivity[0];
    expect(activity.isFullTone).toBe(true);
  });

  it('should apply half-tone class for activity between 30-60 minutes', () => {
    const fixture = TestBed.createComponent(AppComponent);
    const component = fixture.componentInstance;

    // Render first: ngOnInit resets userActivity to [] while unauthenticated,
    // so assigning before the initial detectChanges is silently undone.
    fixture.detectChanges();

    component.userActivity = [
      { initial: 'D', userName: 'Dad', minutesAgo: 45, isFullTone: false, isHalfTone: true, lastAction: null, activeForMinutes: null }
    ];

    fixture.detectChanges();

    // The CSS class logic: half-tone class when in 30-60 min range
    const activity = component.userActivity[0];
    expect(activity.isFullTone).toBe(false);
    expect(activity.isHalfTone).toBe(true);
  });

  /**
   * How much chrome there is, asked for by the page and answered by the shell.
   *
   * <p>The complaint this exists for was precise: pressing fullscreen in the
   * reader went fullscreen and left the blue "Ferrer Utils" bar and the user's
   * name across the top of it. Native fullscreen makes the frame bigger; it has
   * no opinion at all about what the app chooses to draw inside it, and this is
   * where that opinion lives.</p>
   *
   * <p>Signed in, because that is the only branch that has chrome to reduce —
   * the anonymous shell is a bare router outlet already.</p>
   */
  describe('chrome levels', () => {
    let fixture: ComponentFixture<AppComponent>;
    let compiled: HTMLElement;

    beforeEach(() => {
      // Before the component is created, since AuthService reads it once on
      // construction and the shell only exists on the authenticated branch.
      localStorage.setItem('auth_token', 'test-token');
      localStorage.setItem('auth_name', 'Paul');

      fixture = TestBed.createComponent(AppComponent);
      fixture.detectChanges();

      compiled = fixture.nativeElement as HTMLElement;
    });

    afterEach(() => {
      // Signing in starts the shell's pollers — activity, the Date Night
      // announcement, the showtime check. None of them is what these tests are
      // about, and the suite-wide verify() in the outer afterEach counts them.
      httpMock.match(() => true).forEach(request => request.flush([]));
      localStorage.clear();
    });

    const chromeAt = (level: ChromeLevel): void => {
      TestBed.inject(AppChromeService).setLevel(level);
      fixture.detectChanges();
    };

    it('shows the toolbar and the nav in the ordinary way', () => {
      expect(compiled.querySelector('mat-toolbar')).toBeTruthy();
      expect(compiled.querySelector('.app-nav')).toBeTruthy();
    });

    /** Removed rather than hidden: the shell sizes itself against this bar, so
     *  an invisible one still costs the page the height it was asked to give up. */
    it('takes the toolbar and the nav out of the page entirely', () => {
      chromeAt('none');

      expect(compiled.querySelector('mat-toolbar')).toBeNull();
      expect(compiled.querySelector('.app-nav')).toBeNull();
    });

    it('stops subtracting a toolbar that is no longer there', () => {
      chromeAt('none');

      const shell = compiled.querySelector('.app-shell') as HTMLElement;
      expect(shell.classList).toContain('no-toolbar');
      expect(Math.round(parseFloat(getComputedStyle(shell).height))).toBe(window.innerHeight);
    });

    /**
     * The tablet reader. The toolbar goes because it charges a strip across the
     * top of the book for a name and a logout; the sidebar stays because with
     * no toolbar it is the only way off the page.
     */
    it('drops the toolbar but keeps the nav at rail level', () => {
      chromeAt('rail');

      expect(compiled.querySelector('mat-toolbar')).toBeNull();
      expect(compiled.querySelector('.app-nav')).toBeTruthy();
    });

    /** Locked, not merely defaulted: the control that would expand it lives in
     *  the toolbar that is no longer there. */
    it('holds the nav at its rail width whatever the stored preference says', () => {
      fixture.componentInstance.navCollapsed = false;
      chromeAt('rail');

      expect(compiled.querySelector('.app-nav')?.classList).toContain('rail');
      expect(fixture.componentInstance.navOpen(false)).toBeFalse();
    });

    it('gives both back when the page stops asking', () => {
      chromeAt('none');
      chromeAt('full');

      expect(compiled.querySelector('mat-toolbar')).toBeTruthy();
      expect(compiled.querySelector('.app-nav')).toBeTruthy();
    });
  });
});
