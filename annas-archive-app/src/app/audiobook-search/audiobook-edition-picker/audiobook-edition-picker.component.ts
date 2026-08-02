import { CommonModule } from '@angular/common';
import { Component, Inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatRadioModule } from '@angular/material/radio';
import { AudiobookSearchResult } from '../../services/audiobook-request-api.service';

export interface AudiobookEditionPickerData {
  suggestedTitle: string;
  choices: AudiobookSearchResult[];
}

/**
 * Edition disambiguation for an AI suggestion that matched more than one real
 * Audible edition. Narrator, runtime, language, and abridgement are the facts
 * that distinguish these rows, so they are shown before the choice — never
 * collapsed into one silently selected edition.
 */
@Component({
  selector: 'app-audiobook-edition-picker',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatDialogModule, MatIconModule, MatRadioModule],
  templateUrl: './audiobook-edition-picker.component.html',
  styleUrl: './audiobook-edition-picker.component.scss'
})
export class AudiobookEditionPickerComponent {
  selectedAsin: string | null = null;

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: AudiobookEditionPickerData,
    private dialogRef: MatDialogRef<AudiobookEditionPickerComponent, AudiobookSearchResult | undefined>
  ) {}

  choose(): void {
    const chosen = this.data.choices.find(choice => choice.asin === this.selectedAsin);
    if (chosen) this.dialogRef.close(chosen);
  }

  cancel(): void {
    this.dialogRef.close(undefined);
  }

  runtimeLabel(minutes?: number): string | null {
    if (!minutes || minutes < 1) return null;
    const hours = Math.floor(minutes / 60);
    return hours > 0 ? `${hours}h ${minutes % 60}m` : `${minutes}m`;
  }

  availabilityLabel(choice: AudiobookSearchResult): string | null {
    if (choice.availability === 'owned') return 'Already in your library';
    if (choice.availability === 'requested') return 'Already requested';
    return null;
  }
}
