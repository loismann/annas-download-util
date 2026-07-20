import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { VideoDto } from '../../services/video-library-api.service';

@Component({
  selector: 'app-video-card',
  standalone: true,
  imports: [
    CommonModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatCheckboxModule
  ],
  templateUrl: './video-card.component.html',
  styleUrls: ['./video-card.component.css']
})
export class VideoCardComponent {
  @Input() video!: VideoDto;
  @Input() tileSize: 'small' | 'medium' | 'large' = 'medium';
  @Input() bulkEditMode = false;
  @Input() isSelected = false;
  @Input() placeholderUrl = '/assets/video-placeholder.jpg';

  @Output() thumbnailClick = new EventEmitter<VideoDto>();
  @Output() ratingChange = new EventEmitter<{ video: VideoDto; rating: number }>();
  @Output() bookmarkToggle = new EventEmitter<VideoDto>();
  @Output() editClick = new EventEmitter<VideoDto>();
  @Output() playClick = new EventEmitter<VideoDto>();
  @Output() selectionToggle = new EventEmitter<VideoDto>();
  @Output() thumbnailError = new EventEmitter<Event>();

  readonly starRange = [1, 2, 3, 4, 5];

  onThumbnailClick(): void {
    this.thumbnailClick.emit(this.video);
  }

  onThumbnailError(event: Event): void {
    const img = event.target as HTMLImageElement;
    if (!img || img.src.endsWith(this.placeholderUrl)) {
      return;
    }
    img.src = this.placeholderUrl;
    this.thumbnailError.emit(event);
  }

  setPersonalRating(rating: number): void {
    this.ratingChange.emit({ video: this.video, rating });
  }

  onBookmarkToggle(): void {
    this.bookmarkToggle.emit(this.video);
  }

  onEditClick(): void {
    this.editClick.emit(this.video);
  }

  onPlayClick(): void {
    this.playClick.emit(this.video);
  }

  onSelectionToggle(): void {
    this.selectionToggle.emit(this.video);
  }

  get metaLine(): string {
    const parts: string[] = [];
    if (this.video.resolution) {
      parts.push(this.video.resolution);
    }
    if (this.video.format) {
      parts.push(this.video.format);
    }
    if (this.video.fileSize) {
      parts.push(this.video.fileSize);
    }
    return parts.join(' | ');
  }
}
