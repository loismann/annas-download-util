import { Component, EventEmitter, Input, OnDestroy, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { Subject } from 'rxjs';
import { debounceTime, distinctUntilChanged, takeUntil } from 'rxjs/operators';

import { AiApiService, AuthorSuggestion } from '../../services/ai-api.service';
import { LoggerService } from '../../services/logger.service';
import { SearchFiltersComponent } from '../search-filters/search-filters.component';
import { VpnToggleComponent } from '../vpn-toggle/vpn-toggle.component';

export interface DomainHealth {
  name: string;
  extension: string;
  health: number | null;
  certExpDays: number | null;
}

export interface SearchFormSubmitEvent {
  searchTerm: string;
  selectedAuthor: string;
  selectedFormat: string;
  useLibGen: boolean;
  isAiSearch: boolean;
  aiSearchQuery?: string;
}

@Component({
  selector: 'app-search-form',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatSlideToggleModule,
    MatProgressSpinnerModule,
    SearchFiltersComponent,
    VpnToggleComponent
  ],
  templateUrl: './search-form.component.html',
  styleUrls: ['./search-form.component.css']
})
export class SearchFormComponent implements OnDestroy {
  @Input() loading = false;
  @Input() error: string | null = null;
  @Input() annaDomains: DomainHealth[] = [];
  @Input() downloadsLeft: number | null = null;
  @Input() downloadsPerDay: number | null = null;
  @Input() relatedBooksModalOpen = false;
  @Input() collapsed = false;

  @Output() search = new EventEmitter<SearchFormSubmitEvent>();
  @Output() openRelatedBooks = new EventEmitter<{ searchTerm: string; author: string }>();
  @Output() toggleCollapsed = new EventEmitter<void>();
  @Output() formatChange = new EventEmitter<string>();

  // Internal form state
  searchTerm = '';
  aiSearchQuery = '';
  aiSearchExpanded = false;
  selectedAuthor = '';
  selectedFormat = '';
  useLibGen = false;

  // Author suggestions
  authorSuggestions: AuthorSuggestion[] = [];
  loadingAuthors = false;
  private searchTermSubject = new Subject<string>();
  private destroy$ = new Subject<void>();
  private latestAuthorQuery = '';

  // Static format list
  readonly availableFormats = ['EPUB', 'MOBI', 'PDF', 'AZW3', 'FB2', 'TXT'];

  constructor(
    private aiApi: AiApiService,
    private logger: LoggerService
  ) {
    // Set up debounced author fetching
    this.searchTermSubject.pipe(
      debounceTime(500),
      distinctUntilChanged(),
      takeUntil(this.destroy$)
    ).subscribe(term => {
      this.fetchAuthorSuggestions(term);
    });
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  onSubmit(): void {
    if (this.aiSearchExpanded) {
      this.search.emit({
        searchTerm: this.searchTerm.trim(),
        selectedAuthor: this.selectedAuthor,
        selectedFormat: this.selectedFormat,
        useLibGen: this.useLibGen,
        isAiSearch: true,
        aiSearchQuery: this.aiSearchQuery.trim()
      });
    } else {
      this.search.emit({
        searchTerm: this.searchTerm.trim(),
        selectedAuthor: this.selectedAuthor,
        selectedFormat: this.selectedFormat,
        useLibGen: this.useLibGen,
        isAiSearch: false
      });
    }
  }

  onSearchTermChange(newTerm: string): void {
    if (this.aiSearchExpanded) {
      return;
    }
    this.searchTerm = newTerm;

    // Clear author suggestions if search term is too short
    if (newTerm.trim().length < 3) {
      this.authorSuggestions = [];
      this.selectedAuthor = '';
      return;
    }

    // Trigger debounced author fetch
    this.searchTermSubject.next(newTerm.trim());
  }

  onFormatChange(format: string): void {
    this.selectedFormat = format;
    this.formatChange.emit(format);
  }

  onOpenRelatedBooks(): void {
    if (this.searchTerm.trim() && this.selectedAuthor) {
      this.openRelatedBooks.emit({
        searchTerm: this.searchTerm.trim(),
        author: this.selectedAuthor
      });
    }
  }

  toggleAiSearch(): void {
    this.aiSearchExpanded = !this.aiSearchExpanded;
    if (this.aiSearchExpanded) {
      this.aiSearchQuery = this.searchTerm.trim();
    } else {
      this.aiSearchQuery = '';
    }
  }

  getHealthColorClass(health: number | null): string {
    if (health === null) return 'health-unknown';
    if (health >= 90) return 'health-green';
    if (health >= 70) return 'health-yellow';
    if (health >= 50) return 'health-orange';
    return 'health-red';
  }

  private fetchAuthorSuggestions(bookTitle: string): void {
    if (!bookTitle || bookTitle.length < 3) {
      this.authorSuggestions = [];
      return;
    }

    this.latestAuthorQuery = bookTitle;
    this.loadingAuthors = true;
    this.aiApi.suggestAuthors(bookTitle).subscribe({
      next: (resp) => {
        if (bookTitle !== this.latestAuthorQuery) {
          return;
        }
        this.authorSuggestions = resp.authors;
        this.loadingAuthors = false;
        this.logger.log('[author-suggestions]', { bookTitle, authors: resp.authors });
      },
      error: (err) => {
        if (bookTitle !== this.latestAuthorQuery) {
          return;
        }
        this.logger.error('Failed to fetch author suggestions', err);
        this.authorSuggestions = [];
        this.loadingAuthors = false;
      }
    });
  }
}
