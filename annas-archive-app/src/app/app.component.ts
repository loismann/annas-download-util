import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';

import { MatToolbarModule }   from '@angular/material/toolbar';
import { BookSearchComponent } from './book-search/book-search.component';   // ← path must be correct

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [           // ←  BookSearchComponent **must** be here
    CommonModule,
    MatToolbarModule,
    BookSearchComponent
  ],
  template: `
    <mat-toolbar color="primary">Annas Archive Search</mat-toolbar>
    <app-book-search></app-book-search>
  `
})
export class AppComponent { }
