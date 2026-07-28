import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { RouterOutlet, Router, RouterLink } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { StorageFooterComponent } from './components/storage-footer/storage-footer.component';
import { AuthService, UserActivity } from './services/auth.service';
import { LibraryReviewTriggerService } from './services/library-review-trigger.service';
import { DateNightAnnouncementService } from './services/date-night-announcement.service';
import { DateNightShowtimeService } from './services/date-night-showtime.service';
import { LoggerService } from './services/logger.service';
import { Subscription, interval } from 'rxjs';
import { switchMap, filter } from 'rxjs/operators';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    CommonModule,
    RouterOutlet,
    RouterLink,
    MatToolbarModule,
    MatButtonModule,
    MatMenuModule,
    MatIconModule,
    MatTooltipModule,
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
      margin-left: 16px;
      flex-shrink: 0;
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
    .toolbar-version {
      font-size: 12px;
      opacity: 0.85;
      padding-top: 2px;
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
    ::ng-deep .account-menu-panel .account-menu-version {
      font-size: 0.72rem;
      color: #6b7280;
      margin-top: 4px;
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
    @media (max-width: 720px) {
      .nav-label {
        display: none;
      }
      .nav-menu-btn {
        margin-left: 4px;
        min-width: 0;
        padding: 0 8px;
      }
      .user-name,
      .logout-btn,
      .user-activity-indicators,
      .toolbar-version {
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
          <span class="app-title">Ferrer Utils</span>

          <button
            *ngIf="authService.isAuthenticated$ | async"
            mat-button
            class="nav-menu-btn"
            [matMenuTriggerFor]="navigationMenu">
            <mat-icon>menu</mat-icon>
            <span class="nav-label">Navigation</span>
          </button>

          <mat-menu #navigationMenu="matMenu">
            <button mat-menu-item routerLink="/search">
              <mat-icon>search</mat-icon>
              <span>Book Search</span>
            </button>
            <button mat-menu-item routerLink="/reader">
              <mat-icon>menu_book</mat-icon>
              <span>Ebook Reader</span>
            </button>
            <button mat-menu-item routerLink="/library">
              <mat-icon>local_library</mat-icon>
              <span>Ebook Library</span>
            </button>
            <button mat-menu-item routerLink="/audiobooks">
              <mat-icon>headphones</mat-icon>
              <span>Audiobooks</span>
            </button>
            <button *ngIf="authService.isAdmin()" mat-menu-item routerLink="/spotifinator">
              <mat-icon>library_music</mat-icon>
              <span>Spotif-inator</span>
            </button>
        <button *ngIf="authService.isAdmin()" mat-menu-item routerLink="/quiz">
          <mat-icon>quiz</mat-icon>
          <span>Lucy Quiz</span>
        </button>
        <button *ngIf="authService.isAdmin()" mat-menu-item [matMenuTriggerFor]="videosMenu">
          <mat-icon>video_library</mat-icon>
          <span>Videos</span>
        </button>
        <button mat-menu-item routerLink="/media">
          <mat-icon>live_tv</mat-icon>
          <span>TV &amp; Movies</span>
        </button>
        <button mat-menu-item routerLink="/media-library">
          <mat-icon>video_library</mat-icon>
          <span>Video Library</span>
        </button>
        <!-- Everyone: this is Mom and Dad's Date Night page. -->
        <button mat-menu-item routerLink="/date-night">
          <mat-icon>local_movies</mat-icon>
          <span>Date Night</span>
        </button>
        <button *ngIf="authService.isAdmin()" mat-menu-item routerLink="/date-night/pool">
          <mat-icon>inventory_2</mat-icon>
          <span>Date Night pool</span>
        </button>
      </mat-menu>

      <mat-menu #videosMenu="matMenu">
        <button mat-menu-item routerLink="/videos">
          <mat-icon>video_library</mat-icon>
          <span>Video Library</span>
        </button>
        <button mat-menu-item routerLink="/videos/download">
          <mat-icon>download</mat-icon>
          <span>Download Videos</span>
        </button>
      </mat-menu>

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
                <div class="account-menu-version">Version: {{ buildTime }}</div>
              </div>
              <button mat-menu-item (click)="logout()">
                <mat-icon>logout</mat-icon>
                <span>Logout</span>
              </button>
            </mat-menu>
          </div>
        </div>
        <div class="toolbar-version">
          Latest Version: {{ buildTime }}
        </div>
      </div>
    </mat-toolbar>
    <div>
      <router-outlet></router-outlet>
    </div>
    <!-- Storage footer temporarily disabled during the Sonarr/Radarr 1080p
         library migration — free space is swinging wildly as old files get
         replaced, which reads as alarming/confusing on every page. Re-enable
         by restoring the padding-bottom binding above and this element. -->
    <!-- <app-storage-footer *ngIf="authService.isAuthenticated$ | async"></app-storage-footer> -->
  `
})
export class AppComponent implements OnInit, OnDestroy {
  buildTime = '';
  userActivity: UserActivity[] = [];

  private activitySubscription?: Subscription;
  private reviewCheckSubscription?: Subscription;
  private announcementSubscription?: Subscription;
  private showtimeSubscription?: Subscription;

  constructor(
    public authService: AuthService,
    private router: Router,
    private logger: LoggerService,
    private http: HttpClient,
    private libraryReviewTrigger: LibraryReviewTriggerService,
    private dateNightAnnouncement: DateNightAnnouncementService,
    private dateNightShowtime: DateNightShowtimeService
  ) {}

  ngOnInit(): void {
    // Fetched at runtime (rather than compiled in) so the Docker image's
    // frontend build layer can be cached when Angular source is unchanged —
    // see version.json generation in the Dockerfile's runtime stage.
    this.http.get<{ buildTime: string }>('/assets/version.json').subscribe({
      next: (version) => { this.buildTime = version.buildTime; },
      error: (err) => this.logger.error('Failed to fetch version.json', err)
    });

    // Poll for user activity every 60 seconds when authenticated
    this.activitySubscription = this.authService.isAuthenticated$.pipe(
      filter(isAuth => isAuth),
      switchMap(() => {
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

    // Showtime countdown poll — app-wide (not just /date-night), since the popup
    // needs to appear "on both accounts" regardless of which page they're on.
    // Polled every 45s rather than tied to any user action, because there's no
    // push notification to drive this any other way.
    this.showtimeSubscription = this.authService.isAuthenticated$.pipe(
      filter(isAuth => isAuth),
      switchMap(() => {
        this.dateNightShowtime.checkAndMaybeShow();
        return interval(45000);
      })
    ).subscribe(() => this.dateNightShowtime.checkAndMaybeShow());
  }

  ngOnDestroy(): void {
    this.activitySubscription?.unsubscribe();
    this.reviewCheckSubscription?.unsubscribe();
    this.announcementSubscription?.unsubscribe();
    this.showtimeSubscription?.unsubscribe();
  }

  private fetchUserActivity(): void {
    this.authService.getUserActivity().subscribe({
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
