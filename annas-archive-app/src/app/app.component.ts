import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterOutlet, Router, RouterLink } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from './services/auth.service';

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
    MatIconModule
  ],
  template: `
    <mat-toolbar color="primary">
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
          <span>Dropbox Reader</span>
        </button>
        <button mat-menu-item routerLink="/gaming">
          <mat-icon>videogame_asset</mat-icon>
          <span>Gaming PC</span>
        </button>
      </mat-menu>

      <span style="flex: 1"></span>
      <span *ngIf="authService.isAuthenticated$ | async" style="margin-right: 16px">
        {{ authService.getName() }}
      </span>
      <button
        *ngIf="authService.isAuthenticated$ | async"
        mat-button
        (click)="logout()">
        Logout
      </button>
    </mat-toolbar>
    <router-outlet></router-outlet>
  `
})
export class AppComponent {
  constructor(
    public authService: AuthService,
    private router: Router
  ) {}

  logout(): void {
    this.authService.logout();
    this.router.navigate(['/login']);
  }
}
