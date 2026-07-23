import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterOutlet, Router, RouterLink } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { StorageFooterComponent } from './components/storage-footer/storage-footer.component';
import { AuthService, UserActivity } from './services/auth.service';
import { LoggerService } from './services/logger.service';
import { VERSION } from './version';
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
  `],
  template: `
    <mat-toolbar color="primary">
      <div style="display: flex; flex-direction: column; width: 100%;">
        <div style="display: flex; align-items: center; width: 100%;">
          <span>Ferrer Utils</span>

          <button
            *ngIf="authService.isAuthenticated$ | async"
            mat-button
            [matMenuTriggerFor]="navigationMenu"
            style="margin-left: 16px">
            <mat-icon>menu</mat-icon>
            Navigation
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
        <button *ngIf="authService.isAdmin()" mat-menu-item routerLink="/media">
          <mat-icon>live_tv</mat-icon>
          <span>TV &amp; Movies</span>
        </button>
        <button *ngIf="authService.isAdmin()" mat-menu-item routerLink="/media-library">
          <mat-icon>video_library</mat-icon>
          <span>Video Library</span>
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

          <span style="flex: 1"></span>
          <div *ngIf="authService.isAuthenticated$ | async" style="display: flex; align-items: center;">
            <span style="margin-right: 8px">
              {{ authService.getName() }}
            </span>
            <button mat-button (click)="logout()">
              Logout
            </button>
            <div class="user-activity-indicators" *ngIf="userActivity.length > 0">
              <div
                *ngFor="let activity of userActivity"
                class="activity-dot"
                [class.full-tone]="activity.isFullTone"
                [class.half-tone]="activity.isHalfTone"
                [matTooltip]="activity.userName + ' - ' + (activity.minutesAgo === null ? 'no recent activity' : activity.minutesAgo < 1 ? 'just now' : Math.round(activity.minutesAgo) + 'm ago')">
                {{ activity.initial }}
              </div>
            </div>
          </div>
        </div>
        <div style="font-size: 12px; opacity: 0.85; padding-top: 2px;">
          Latest Version: {{ buildTime }}
        </div>
      </div>
    </mat-toolbar>
    <div [style.padding-bottom.px]="(authService.isAuthenticated$ | async) ? 32 : 0">
      <router-outlet></router-outlet>
    </div>
    <app-storage-footer *ngIf="authService.isAuthenticated$ | async"></app-storage-footer>
  `
})
export class AppComponent implements OnInit, OnDestroy {
  buildTime = VERSION.buildTime;
  userActivity: UserActivity[] = [];
  Math = Math; // Expose Math to template

  private activitySubscription?: Subscription;

  constructor(
    public authService: AuthService,
    private router: Router,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
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
  }

  ngOnDestroy(): void {
    this.activitySubscription?.unsubscribe();
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
}
