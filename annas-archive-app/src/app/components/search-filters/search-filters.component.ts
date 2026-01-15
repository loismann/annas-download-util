import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';

export type DownloadWarningLevel = 'none' | 'yellow' | 'orange' | 'red';

@Component({
  selector: 'app-search-filters',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatSelectModule
  ],
  templateUrl: './search-filters.component.html',
  styleUrls: ['./search-filters.component.css']
})
export class SearchFiltersComponent {
  @Input() selectedFormat = '';
  @Input() availableFormats: string[] = [];
  @Input() downloadsLeft: number | null = null;
  @Input() downloadsPerDay: number | null = null;
  @Input() disabled = false;

  @Output() formatChange = new EventEmitter<string>();

  get downloadWarningLevel(): DownloadWarningLevel {
    if (this.downloadsLeft === null) return 'none';
    if (this.downloadsLeft <= 10) return 'red';
    if (this.downloadsLeft <= 20) return 'orange';
    if (this.downloadsLeft <= 30) return 'yellow';
    return 'none';
  }

  onFormatChange(format: string): void {
    this.formatChange.emit(format);
  }
}
