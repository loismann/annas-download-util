import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterOutlet, Router } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from './services/auth.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    CommonModule,
    RouterOutlet,
    MatToolbarModule,
    MatButtonModule
  ],
  template: `
    <mat-toolbar color="primary">
      <span>Annas Archive Search</span>
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
