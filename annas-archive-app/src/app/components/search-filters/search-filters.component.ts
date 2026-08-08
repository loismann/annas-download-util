import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

export type DownloadWarningLevel = 'none' | 'yellow' | 'orange' | 'red';

/** Just the download counter now — the format selector this used to also
 *  render moved to the results toolbar (book-search.component.html), since
 *  it's only meaningful once there are results to filter. */
@Component({
  selector: 'app-search-filters',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './search-filters.component.html',
  styleUrl: './search-filters.component.scss'
})
export class SearchFiltersComponent {
  @Input() downloadsLeft: number | null = null;
  @Input() downloadsPerDay: number | null = null;

  get downloadWarningLevel(): DownloadWarningLevel {
    if (this.downloadsLeft === null) return 'none';
    if (this.downloadsLeft <= 10) return 'red';
    if (this.downloadsLeft <= 20) return 'orange';
    if (this.downloadsLeft <= 30) return 'yellow';
    return 'none';
  }
}
