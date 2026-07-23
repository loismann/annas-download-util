import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MediaLookupResult } from '../../services/media-search-api.service';

export type MediaAddState = 'idle' | 'adding' | 'added' | 'error';

@Component({
  selector: 'app-media-result-card',
  standalone: true,
  imports: [CommonModule, MatCardModule, MatButtonModule, MatIconModule],
  templateUrl: './media-result-card.component.html',
  styleUrl: './media-result-card.component.css'
})
export class MediaResultCardComponent {
  @Input() result!: MediaLookupResult;
  @Input() addState: MediaAddState = 'idle';
  @Input() progressLabel: string | null = null;
  @Input() placeholderUrl = '/assets/placeholder.jpg';
  /** TV only — seasons already monitored (requested) in Sonarr, if any. */
  @Input() alreadyAddedSeasons?: number[];
  /** TV only — of the requested seasons, which actually have downloaded
   * files yet — vs. still just monitored/searching. */
  @Input() downloadedSeasons?: number[];

  @Output() add = new EventEmitter<MediaLookupResult>();

  get alreadyAddedLabel(): string | null {
    if (!this.alreadyAddedSeasons?.length) return null;
    const downloaded = this.downloadedSeasons || [];
    const requestedOnly = this.alreadyAddedSeasons.filter(n => !downloaded.includes(n));

    const format = (nums: number[]) =>
      [...nums].sort((a, b) => a - b).map(n => (n === 0 ? 'Specials' : `S${n}`)).join(', ');

    const parts: string[] = [];
    if (downloaded.length) parts.push(`Downloaded: ${format(downloaded)}`);
    if (requestedOnly.length) parts.push(`Requested: ${format(requestedOnly)}`);
    return parts.join(' · ');
  }

  get buttonLabel(): string {
    return this.alreadyAddedSeasons?.length ? 'Manage Seasons' : 'Add';
  }

  get posterUrl(): string {
    const poster = this.result.images?.find(i => i.coverType === 'poster');
    return poster?.remoteUrl || poster?.url || this.placeholderUrl;
  }

  onImgError(event: Event): void {
    const img = event.target as HTMLImageElement;
    if (img && !img.src.endsWith(this.placeholderUrl)) {
      img.src = this.placeholderUrl;
    }
  }

  onAddClick(): void {
    this.add.emit(this.result);
  }
}
