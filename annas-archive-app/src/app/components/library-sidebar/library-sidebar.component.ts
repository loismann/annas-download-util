import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';

export interface AdminFilters {
  filterMissingAuthor: boolean;
  filterMissingCover: boolean;
  filterGenreCountEnabled: boolean;
  filterGenreCount: number;
  filterGenreComparison: 'less' | 'more';
}

@Component({
  selector: 'app-library-sidebar',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatCheckboxModule
  ],
  templateUrl: './library-sidebar.component.html',
  styleUrls: ['./library-sidebar.component.css']
})
export class LibrarySidebarComponent {
  // Filter inputs
  @Input() genres: string[] = [];
  @Input() searchTerm = '';
  @Input() selectedGenre = '';
  @Input() minPersonalRating = 0;
  @Input() minGoodreadsRating = 0;
  @Input() totalBooks = 0;

  // Bulk edit inputs
  @Input() bulkEditMode = false;
  @Input() selectedBooksCount = 0;
  @Input() visibleBooksCount = 0;

  // Admin inputs
  @Input() isAdmin = false;
  @Input() adminOpen = false;
  @Input() filterMissingAuthor = false;
  @Input() filterMissingCover = false;
  @Input() filterGenreCountEnabled = false;
  @Input() filterGenreCount = 1;
  @Input() filterGenreComparison: 'less' | 'more' = 'less';

  // Filter outputs
  @Output() resetView = new EventEmitter<void>();
  @Output() searchTermChange = new EventEmitter<string>();
  @Output() selectedGenreChange = new EventEmitter<string>();
  @Output() minPersonalRatingChange = new EventEmitter<number>();
  @Output() minGoodreadsRatingChange = new EventEmitter<number>();

  // Bulk edit outputs
  @Output() bulkEditToggle = new EventEmitter<void>();
  @Output() openBulkEdit = new EventEmitter<void>();
  @Output() selectAllVisible = new EventEmitter<void>();
  @Output() bulkSend = new EventEmitter<'dropbox' | 'kindle-dad' | 'kindle-mom'>();

  // Admin outputs
  @Output() adminToggle = new EventEmitter<void>();
  @Output() filterMissingAuthorChange = new EventEmitter<boolean>();
  @Output() filterMissingCoverChange = new EventEmitter<boolean>();
  @Output() filterGenreCountEnabledChange = new EventEmitter<boolean>();
  @Output() filterGenreCountChange = new EventEmitter<number>();
  @Output() filterGenreComparisonChange = new EventEmitter<'less' | 'more'>();
  @Output() wipeGenres = new EventEmitter<void>();
  @Output() uploadBooks = new EventEmitter<void>();

  onResetView(): void {
    this.resetView.emit();
  }

  onSearchTermChange(term: string): void {
    this.searchTermChange.emit(term);
  }

  onSelectedGenreChange(genre: string): void {
    this.selectedGenreChange.emit(genre);
  }

  onMinPersonalRatingChange(rating: number): void {
    this.minPersonalRatingChange.emit(rating);
  }

  onMinGoodreadsRatingChange(rating: number): void {
    this.minGoodreadsRatingChange.emit(rating);
  }

  onBulkEditToggle(): void {
    this.bulkEditToggle.emit();
  }

  onOpenBulkEdit(): void {
    this.openBulkEdit.emit();
  }

  onSelectAllVisible(): void {
    this.selectAllVisible.emit();
  }

  onBulkSend(action: 'dropbox' | 'kindle-dad' | 'kindle-mom'): void {
    this.bulkSend.emit(action);
  }

  onAdminToggle(): void {
    this.adminToggle.emit();
  }

  onFilterMissingAuthorChange(value: boolean): void {
    this.filterMissingAuthorChange.emit(value);
  }

  onFilterMissingCoverChange(value: boolean): void {
    this.filterMissingCoverChange.emit(value);
  }

  onFilterGenreCountEnabledChange(value: boolean): void {
    this.filterGenreCountEnabledChange.emit(value);
  }

  onFilterGenreCountChange(value: number): void {
    this.filterGenreCountChange.emit(value);
  }

  onFilterGenreComparisonChange(value: 'less' | 'more'): void {
    this.filterGenreComparisonChange.emit(value);
  }

  onWipeGenres(): void {
    this.wipeGenres.emit();
  }

  onUploadBooks(): void {
    this.uploadBooks.emit();
  }
}
