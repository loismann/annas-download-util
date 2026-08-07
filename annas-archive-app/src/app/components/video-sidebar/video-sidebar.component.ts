import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'app-video-sidebar',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule
  ],
  templateUrl: './video-sidebar.component.html',
  styleUrls: ['./video-sidebar.component.css']
})
export class VideoSidebarComponent {
  @Input() genres: string[] = [];
  @Input() searchTerm = '';
  @Input() selectedGenre = '';
  @Input() minPersonalRating = 0;
  @Input() totalVideos = 0;
  @Input() bulkEditMode = false;
  @Input() selectedVideosCount = 0;
  @Input() visibleVideosCount = 0;
  @Input() isAdmin = false;

  @Output() resetView = new EventEmitter<void>();
  @Output() searchTermChange = new EventEmitter<string>();
  @Output() selectedGenreChange = new EventEmitter<string>();
  @Output() minPersonalRatingChange = new EventEmitter<number>();
  @Output() bulkEditToggle = new EventEmitter<void>();
  @Output() selectAllVisible = new EventEmitter<void>();

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

  onBulkEditToggle(): void {
    this.bulkEditToggle.emit();
  }

  onSelectAllVisible(): void {
    this.selectAllVisible.emit();
  }
}
