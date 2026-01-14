import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';

import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';

import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatCardModule,
  ],
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.css'],
})
export class LoginComponent {
  code = '';
  loading = false;
  error: string | null = null;

  constructor(
    private authService: AuthService,
    private router: Router,
    private logger: LoggerService
  ) {}

  onLogin(): void {
    this.error = null;

    if (!this.code.trim()) {
      this.error = 'Please enter your access code.';
      return;
    }

    this.loading = true;

    this.authService.login(this.code.trim()).subscribe({
      next: () => {
        this.loading = false;
        this.router.navigate(['/search']);
      },
      error: err => {
        this.loading = false;
        if (err.status === 401) {
          this.error = 'Invalid access code.';
        } else {
          this.error = 'Login failed. Please try again.';
        }
        this.logger.error('Login error:', err);
      }
    });
  }
}
