import { Component, DestroyRef, OnDestroy, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { RouterOutlet, Router, NavigationEnd } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { BreakpointObserver } from '@angular/cdk/layout';
import { map, shareReplay } from 'rxjs/operators';
import { StorageFooterComponent } from './components/storage-footer/storage-footer.component';
import { SidebarNavComponent } from './components/sidebar-nav/sidebar-nav.component';
import { AuthService, UserActivity } from './services/auth.service';
import { LibraryReviewTriggerService } from './services/library-review-trigger.service';
import { DateNightAnnouncementService } from './services/date-night-announcement.service';
import { DateNightShowtimeService } from './services/date-night-showtime.service';
import { DateNightReminderService } from './services/date-night-reminder.service';
import { LoggerService } from './services/logger.service';
import { EMPTY, Observable, Subscription, fromEvent, interval, merge, of, timer } from 'rxjs';
import { switchMap, filter, throttleTime, startWith } from 'rxjs/operators';


@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    CommonModule,
    RouterOutlet,
    MatToolbarModule,
    MatButtonModule,
    MatMenuModule,
    MatIconModule,
    MatTooltipModule,
    SidebarNavComponent,
    StorageFooterComponent
  ],
  styles: [`
    .toolbar-column {
      display: flex;
      flex-direction: column;
      width: 100%;
    }
    .toolbar-row {
      display: flex;
      align-items: center;
      width: 100%;
      min-width: 0;
      gap: 4px;
    }
    .app-title {
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      min-width: 0;
    }
    .nav-menu-btn {
      flex-shrink: 0;
      margin-right: 4px;
      /* Material's icon button carries its own colour token and does not pick up
         the toolbar's contrast colour, so it rendered dark grey on the blue bar.
         The stroke below is currentColor, which made the wrong colour bolder. */
      color: #fff;
    }
    /* The Material Icons font has no weight axis (it's the classic font, not
       Material Symbols), so the glyph is stroked to thicken it rather than
       swapped for a heavier variant. */
    .nav-menu-btn mat-icon {
      color: inherit;
      font-size: 26px;
      width: 26px;
      height: 26px;
      -webkit-text-stroke: 1.1px currentColor;
    }

    /* 100dvh (not vh) matches the mobile pass — it accounts for iOS Safari's
       retracting browser chrome. */
    .app-shell {
      display: flex;
      height: calc(100dvh - 64px);
      position: relative;
    }
    @media (max-width: 599px) {
      /* Material's toolbar is 56px tall below this width, not 64px. */
      .app-shell { height: calc(100dvh - 56px); }
    }

    .app-nav {
      flex: 0 0 auto;
      display: flex;
      flex-direction: column;
      width: 248px;
      border-right: 1px solid rgba(0, 0, 0, 0.12);
      background: #fafafa;
      overflow-y: auto;
      overflow-x: hidden;
      transition: width 200ms cubic-bezier(0.4, 0, 0.2, 1);
    }
    /* Wide enough for an icon plus a short caption beneath it. */
    .app-nav.rail { width: 76px; }

    /* Storage + build stamp pinned to the bottom, below the links. */
    .nav-footer {
      margin-top: auto;
      border-top: 1px solid rgba(0, 0, 0, 0.10);
    }
    .nav-version {
      padding: 8px 16px 12px;
      font-size: 0.68rem;
      line-height: 1.35;
      color: #80868b;
    }

    /* Date Night pages are full-bleed black — see SidebarNavComponent's dark
       rules for the matching link/icon palette. */
    .app-nav.dark {
      background: #000;
      border-right-color: rgba(217, 164, 65, 0.25);
    }
    .app-nav.dark .nav-footer { border-top-color: rgba(217, 164, 65, 0.25); }
    .app-nav.dark .nav-version { color: rgba(232, 220, 192, 0.6); }

    /* On a phone the nav floats above the page instead of taking a column, so
       the page never gets squeezed into an unusable width. */
    .app-nav.overlay {
      position: absolute;
      top: 0;
      bottom: 0;
      left: 0;
      z-index: 3;
      width: 248px;
      box-shadow: 2px 0 8px rgba(0, 0, 0, 0.25);
      transition: transform 200ms cubic-bezier(0.4, 0, 0.2, 1);
    }
    .app-nav.overlay.hidden { transform: translateX(-100%); }

    .nav-backdrop {
      position: absolute;
      inset: 0;
      z-index: 2;
      background: rgba(0, 0, 0, 0.4);
    }

    /* min-width: 0 stops a wide child (a grid, a table) from forcing the flex
       item wider than the viewport instead of scrolling inside it. */
    .app-content {
      flex: 1 1 auto;
      min-width: 0;
      overflow: auto;
    }
    .toolbar-spacer {
      flex: 1 1 0;
      min-width: 8px;
    }
    .toolbar-user {
      display: flex;
      align-items: center;
      flex-shrink: 0;
    }
    .user-name {
      margin-right: 8px;
      white-space: nowrap;
    }
    .user-activity-indicators {
      display: flex;
      gap: 6px;
      margin-left: 8px;
      align-items: center;
    }
    .activity-dot {
      width: 22px;
      height: 22px;
      border-radius: 50%;
      display: flex;
      align-items: center;
      justify-content: center;
      font-size: 12px;
      font-weight: bold;
      cursor: default;
      border: 2px solid #4caf50;
      color: #4caf50;
      background-color: transparent;
    }
    .activity-dot.full-tone {
      background-color: #4caf50;
      color: white;
    }
    .activity-dot.half-tone {
      background-color: rgba(76, 175, 80, 0.5);
      color: white;
    }
    .account-menu-btn {
      display: none;
    }
    /* The account menu renders in an overlay, so these can't be scoped :host styles —
       ::ng-deep with the panel class keeps them from leaking to other menus. */
    ::ng-deep .account-menu-panel .account-menu-header {
      padding: 10px 16px;
      border-bottom: 1px solid #e0e0e0;
      margin-bottom: 4px;
    }
    ::ng-deep .account-menu-panel .account-menu-name {
      font-weight: 600;
      font-size: 0.95rem;
      color: #1f2937;
    }
    ::ng-deep .account-menu-panel .account-menu-activity {
      display: flex;
      gap: 6px;
      margin-top: 8px;
    }
    ::ng-deep .account-menu-panel .activity-dot {
      width: 22px;
      height: 22px;
      border-radius: 50%;
      display: flex;
      align-items: center;
      justify-content: center;
      font-size: 12px;
      font-weight: bold;
      border: 2px solid #4caf50;
      color: #4caf50;
    }
    ::ng-deep .account-menu-panel .activity-dot.full-tone {
      background-color: #4caf50;
      color: white;
    }
    ::ng-deep .account-menu-panel .activity-dot.half-tone {
      background-color: rgba(76, 175, 80, 0.5);
      color: white;
    }
    /* Same boundary as isHandset$, so the toolbar compacts at exactly the point
       the sidebar becomes a drawer — otherwise there's a band where the nav has
       collapsed but the toolbar still shows its full desktop content. */
    @media (max-width: 767.98px) {
      .nav-menu-btn {
        margin-left: 4px;
      }
      .user-name,
      .logout-btn,
      .user-activity-indicators {
        display: none;
      }
      .account-menu-btn {
        display: inline-flex;
        align-items: center;
        justify-content: center;
      }
    }
  `],
  template: `
    <mat-toolbar color="primary">
      <div class="toolbar-column">
        <div class="toolbar-row">
          <!-- Leftmost, ahead of the title: the toggle is the anchor of the nav,
               so it sits where the nav is. Morphs between ☰ and ✕ to show what
               it will do next. -->
          <button
            *ngIf="authService.isAuthenticated$ | async"
            mat-icon-button
            class="nav-menu-btn"
            [attr.aria-label]="navOpen(isHandset$ | async) ? 'Close navigation' : 'Open navigation'"
            [attr.aria-expanded]="navOpen(isHandset$ | async)"
            (click)="onMenuButton()">
            <mat-icon>{{ navOpen(isHandset$ | async) ? 'close' : 'menu' }}</mat-icon>
          </button>

          <span class="app-title">Ferrer Utils</span>

          <span class="toolbar-spacer"></span>
          <div *ngIf="authService.isAuthenticated$ | async" class="toolbar-user">
            <span class="user-name">
              {{ authService.getName() }}
            </span>
            <button mat-button class="logout-btn" (click)="logout()">
              Logout
            </button>
            <div class="user-activity-indicators" *ngIf="userActivity.length > 0">
              <div
                *ngFor="let activity of userActivity"
                class="activity-dot"
                [class.full-tone]="activity.isFullTone"
                [class.half-tone]="activity.isHalfTone"
                [matTooltip]="activityTooltip(activity)">
                {{ activity.initial }}
              </div>
            </div>

            <!-- Mobile-only: name, activity, version, and logout collapse into this menu -->
            <button
              mat-icon-button
              class="account-menu-btn"
              [matMenuTriggerFor]="accountMenu"
              aria-label="Account menu">
              <mat-icon>account_circle</mat-icon>
            </button>
            <mat-menu #accountMenu="matMenu" class="account-menu-panel">
              <div class="account-menu-header" (click)="$event.stopPropagation()">
                <div class="account-menu-name">{{ authService.getName() }}</div>
                <div class="account-menu-activity" *ngIf="userActivity.length > 0">
                  <div
                    *ngFor="let activity of userActivity"
                    class="activity-dot"
                    [class.full-tone]="activity.isFullTone"
                    [class.half-tone]="activity.isHalfTone"
                    [matTooltip]="activityTooltip(activity)">
                    {{ activity.initial }}
                  </div>
                </div>
              </div>
              <button mat-menu-item (click)="logout()">
                <mat-icon>logout</mat-icon>
                <span>Logout</span>
              </button>
            </mat-menu>
          </div>
        </div>
      </div>
    </mat-toolbar>
    <!-- A plain flex row rather than mat-sidenav-container. MatSidenav in "side"
         mode sets a fixed margin on its content and does not recalculate it when
         the drawer's width changes, so collapsing to the rail left a dead gap
         where the panel used to be. With flex, the page simply takes whatever
         width the aside gives up and snaps back to the left edge. -->
    <div class="app-shell" *ngIf="authService.isAuthenticated$ | async; else anonymousShell">
      <!-- Phones: fixed overlay above the page, with a backdrop.
           Tablet/desktop: an in-flow column that the page sits beside. -->
      <aside
        class="app-nav"
        [class.rail]="!navOpen(isHandset$ | async)"
        [class.overlay]="isHandset$ | async"
        [class.hidden]="(isHandset$ | async) && !drawerOpen"
        [class.dark]="darkTheme">
        <app-sidebar-nav
          [collapsed]="!navOpen(isHandset$ | async)"
          [dark]="darkTheme"
          (navigated)="onNavigated()"></app-sidebar-nav>

        <!-- Only when there's room for it: the rail has no width for a storage
             breakdown or a full timestamp, and squeezing them in would defeat
             the point of collapsing. -->
        <div class="nav-footer" *ngIf="navOpen(isHandset$ | async)">
          <!-- Admin only. Not rendering it also means Mom and Dad never fire the
               stats request at all, rather than fetching it and hiding it. -->
          <app-storage-footer *ngIf="authService.isAdmin()" [dark]="darkTheme"></app-storage-footer>
          <div class="nav-version">Last deployed<br />{{ buildTime }}</div>
        </div>
      </aside>

      <div
        class="nav-backdrop"
        *ngIf="(isHandset$ | async) && drawerOpen"
        (click)="drawerOpen = false"></div>

      <main class="app-content">
        <router-outlet></router-outlet>
      </main>
    </div>

    <!-- Login and anything else reached while signed out: no nav chrome. -->
    <ng-template #anonymousShell>
      <div class="app-shell">
        <main class="app-content">
          <router-outlet></router-outlet>
        </main>
      </div>
    </ng-template>
  `
})
export class AppComponent implements OnInit, OnDestroy {
  /**
   * Ends in-flight reads when the component is destroyed.
   *
   * Reads only: unsubscribing an HttpClient call aborts the request, so routing
   * a write through this would mean navigating away cancels the user's action.
   */
  private readonly destroyRef = inject(DestroyRef);

  buildTime = '';
  userActivity: UserActivity[] = [];

  private static readonly NAV_COLLAPSED_KEY = 'nav.collapsed';

  /** Whether the permanent sidebar is showing as an icon rail.
   *
   * Defaults to the rail: it reaches every destination in one click and hands
   * the width back to the page, so it's the better resting state. Persisted,
   * since this is a workspace preference — having it snap back on every page
   * load is the main thing that makes a collapsible sidebar annoying. */
  navCollapsed = true;

  /** Phone-only overlay drawer. Always starts closed: an overlay covering the
   * page on arrival would be hostile. */
  drawerOpen = false;

  /** Mirrors isHandset$ for the click handler, which needs the value
   * synchronously rather than through the async pipe. */
  private isHandset = false;
  private handsetSub?: Subscription;

  /** True on the Date Night pages, which are full-bleed black. A light sidebar
   * beside them reads as a rendering fault, so the nav switches to the theater
   * palette to match. */
  darkTheme = false;
  private routeSub?: Subscription;

  /** Phone-sized: the sidebar becomes a slide-over drawer and the toolbar grows
   * a hamburger. 768px is the app's established mobile breakpoint, and putting
   * the boundary just below it means an iPad in portrait (768px) still gets the
   * permanent sidebar, which is what was asked for.
   *
   * Assigned in the constructor rather than as a field initializer, since this
   * depends on an injected dependency and field-initializer ordering relative to
   * parameter properties varies with `useDefineForClassFields`. */
  readonly isHandset$: Observable<boolean>;

  private activitySubscription?: Subscription;
  private reviewCheckSubscription?: Subscription;
  private announcementSubscription?: Subscription;
  private dateNightReminderSubscription?: Subscription;
  private showtimeSubscription?: Subscription;

  constructor(
    public authService: AuthService,
    private router: Router,
    private breakpoints: BreakpointObserver,
    private logger: LoggerService,
    private http: HttpClient,
    private libraryReviewTrigger: LibraryReviewTriggerService,
    private dateNightAnnouncement: DateNightAnnouncementService,
    private dateNightReminder: DateNightReminderService,
    private dateNightShowtime: DateNightShowtimeService
  ) {
    this.isHandset$ = this.breakpoints.observe('(max-width: 767.98px)').pipe(
      map(result => result.matches),
      shareReplay({ bufferSize: 1, refCount: true })
    );

    // Closing the drawer when growing past phone width stops a stale `true`
    // from leaving the backdrop up once the layout no longer uses an overlay.
    this.handsetSub = this.isHandset$.subscribe(isHandset => {
      this.isHandset = isHandset;
      if (!isHandset) this.drawerOpen = false;
    });

    // startWith so a hard refresh straight onto /date-night is themed on the
    // first paint rather than only after the next navigation.
    this.routeSub = this.router.events.pipe(
      filter(e => e instanceof NavigationEnd),
      startWith(null)
    ).subscribe(() => {
      // Covers /date-night and /date-night/pool alike.
      this.darkTheme = this.router.url.split('?')[0].startsWith('/date-night');
    });

    try {
      // Only override the default when a choice has actually been stored —
      // reading the raw value first is what keeps "never set" (rail) distinct
      // from "deliberately expanded".
      const stored = localStorage.getItem(AppComponent.NAV_COLLAPSED_KEY);
      if (stored !== null) this.navCollapsed = stored === 'true';
    } catch {
      // Storage unavailable; fall back to the rail default.
    }
  }

  /** Whether the nav is currently showing full labels: the open drawer on a
   * phone, or the expanded panel elsewhere. Drives both the ☰/✕ icon and
   * whether the sidebar renders as a rail. */
  navOpen(isHandset: boolean | null): boolean {
    return isHandset ? this.drawerOpen : !this.navCollapsed;
  }

  /** Phones only: an overlay drawer should get out of the way once it has been
   * used. The permanent sidebar stays as it is. */
  onNavigated(): void {
    this.drawerOpen = false;
  }

  /** Phone: opens/closes the overlay drawer. Tablet/desktop: collapses the
   * panel to the icon rail and back, handing the width to the page. */
  onMenuButton(): void {
    if (this.isHandset) {
      this.drawerOpen = !this.drawerOpen;
      return;
    }
    this.navCollapsed = !this.navCollapsed;
    try {
      localStorage.setItem(AppComponent.NAV_COLLAPSED_KEY, String(this.navCollapsed));
    } catch {
      // Private browsing / storage disabled — the rail still works, it just
      // won't be remembered next load.
    }
  }

  ngOnInit(): void {
    // Fetched at runtime (rather than compiled in) so the Docker image's
    // frontend build layer can be cached when Angular source is unchanged —
    // see version.json generation in the Dockerfile's runtime stage.
    this.http.get<{ buildTime: string }>('/assets/version.json').subscribe({
      next: (version) => { this.buildTime = version.buildTime; },
      error: (err) => this.logger.error('Failed to fetch version.json', err)
    });

    // Poll for user activity every 60 seconds only while authenticated. Using
    // switchMap on the false value is important: filter(isAuth => isAuth) would
    // leave an interval created during the previous login running after logout.
    this.activitySubscription = this.authService.isAuthenticated$.pipe(
      switchMap(isAuth => {
        if (!isAuth) {
          this.userActivity = [];
          return EMPTY;
        }
        // Initial fetch
        this.fetchUserActivity();
        // Then poll every 60 seconds
        return interval(60000);
      })
    ).subscribe(() => {
      this.fetchUserActivity();
    });

    // Admin-only daily library-review modal — Mom/Dad never issue this call at all
    // (the backend would 403 them via the AdminOnly policy anyway; this just skips
    // a pointless request).
    this.reviewCheckSubscription = this.authService.isAuthenticated$.pipe(
      filter(isAuth => isAuth && this.authService.isAdmin())
    ).subscribe(() => this.libraryReviewTrigger.checkAndMaybeShow());

    // One-time Date Night "coming soon" splash for Mom/Dad. Fires on the first
    // authenticated load of any page, so they see it as early as possible rather
    // than only if they happen to visit a particular screen. The backend decides
    // per person whether it's still owed, and returns shouldShow=false for
    // admins, so this costs one cheap request and nothing else.
    this.announcementSubscription = this.authService.isAuthenticated$.pipe(
      filter(isAuth => isAuth)
    ).subscribe(() => this.dateNightAnnouncement.checkAndMaybeShow());

    // Daily Date Night nudge, app-wide. The first check waits ten seconds so the
    // one-time announcement gets first refusal. After that there is no background
    // polling: checks are driven by real browser activity (focus, returning to the
    // visible tab, pointer, or keyboard), throttled so sustained use cannot create
    // request noise. Logging out switches to EMPTY and removes every listener.
    this.dateNightReminderSubscription = this.authService.isAuthenticated$.pipe(
      switchMap(isAuth => isAuth ? this.dateNightReminderActivity() : EMPTY)
    ).subscribe(() => this.dateNightReminder.checkAndMaybeShow());

    // Showtime countdown poll — app-wide (not just /date-night), since the popup
    // needs to appear "on both accounts" regardless of which page they're on.
    // Polled every 45s rather than tied to any user action, because there's no
    // push notification to drive this any other way. Unlike the old filtered
    // stream, the false branch explicitly cancels the poll at logout.
    this.showtimeSubscription = this.authService.isAuthenticated$.pipe(
      switchMap(isAuth => {
        if (!isAuth) return EMPTY;
        this.dateNightShowtime.checkAndMaybeShow();
        return interval(45000);
      })
    ).subscribe(() => this.dateNightShowtime.checkAndMaybeShow());
  }

  ngOnDestroy(): void {
    this.activitySubscription?.unsubscribe();
    this.reviewCheckSubscription?.unsubscribe();
    this.announcementSubscription?.unsubscribe();
    this.dateNightReminderSubscription?.unsubscribe();
    this.showtimeSubscription?.unsubscribe();
    this.handsetSub?.unsubscribe();
    this.routeSub?.unsubscribe();
  }

  /** One initial authenticated check, then only human activity. The fifteen-minute
   * throttle is not a timer and emits nothing while the device is idle; it merely
   * caps how often a stream of clicks/keystrokes is allowed to ask the server. */
  private dateNightReminderActivity(): Observable<unknown> {
    if (typeof window === 'undefined' || typeof document === 'undefined') return EMPTY;

    const visibleAgain = fromEvent(document, 'visibilitychange').pipe(
      filter(() => document.visibilityState === 'visible')
    );
    return timer(10000).pipe(
      switchMap(() => merge(
        of(null),
        fromEvent(window, 'focus'),
        visibleAgain,
        fromEvent(document, 'pointerdown'),
        fromEvent(document, 'keydown')
      ).pipe(throttleTime(15 * 60 * 1000)))
    );
  }

  private fetchUserActivity(): void {
    this.authService.getUserActivity().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (activity) => {
        this.userActivity = activity;
      },
      error: (err) => {
        this.logger.error('Failed to fetch user activity:', err);
        this.userActivity = [];
      }
    });
  }

  logout(): void {
    this.authService.logout();
    this.router.navigate(['/login']);
  }

  /** "Mom - Reading a book - just now (active 12m)" — what a deploy-safety
   * glance needs: is this person active, what are they doing, and have they
   * been going long enough that interrupting them would actually hurt. */
  activityTooltip(activity: UserActivity): string {
    if (activity.minutesAgo === null) {
      return `${activity.userName} - no recent activity`;
    }

    const recency = activity.minutesAgo < 1 ? 'just now' : `${Math.round(activity.minutesAgo)}m ago`;
    const action = activity.lastAction ?? 'Active';
    const continuity = activity.activeForMinutes !== null && activity.activeForMinutes >= 1
      ? ` (active ${this.formatDuration(activity.activeForMinutes)})`
      : '';

    return `${activity.userName} - ${action} - ${recency}${continuity}`;
  }

  private formatDuration(minutes: number): string {
    const rounded = Math.round(minutes);
    if (rounded < 60) return `${rounded}m`;
    const hours = Math.floor(rounded / 60);
    const mins = rounded % 60;
    return mins > 0 ? `${hours}h ${mins}m` : `${hours}h`;
  }
}
