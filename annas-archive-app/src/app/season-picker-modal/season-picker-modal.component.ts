import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MediaSeasonInfo } from '../services/media-search-api.service';

export interface SeasonPickerModalData {
  title: string;
  seasons: MediaSeasonInfo[];
  /** Present when this show is already in Sonarr — seasons in this list are
   * already monitored, so the picker defaults to reflecting that instead of
   * the usual "everything but Specials" fresh-add default. */
  alreadyAddedSeasons?: number[];
}

interface SeasonChoice {
  seasonNumber: number;
  label: string;
  episodeCount?: number;
  selected: boolean;
  alreadyAdded: boolean;
}

@Component({
  selector: 'app-season-picker-modal',
  standalone: true,
  imports: [CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule],
  templateUrl: './season-picker-modal.component.html',
  styleUrl: './season-picker-modal.component.css'
})
export class SeasonPickerModalComponent {
  choices: SeasonChoice[];

  constructor(
    private dialogRef: MatDialogRef<SeasonPickerModalComponent, number[] | undefined>,
    @Inject(MAT_DIALOG_DATA) public data: SeasonPickerModalData
  ) {
    const alreadyAdded = data.alreadyAddedSeasons;
    this.choices = [...data.seasons]
      .sort((a, b) => a.seasonNumber - b.seasonNumber)
      .map(s => ({
        seasonNumber: s.seasonNumber,
        label: s.seasonNumber === 0 ? 'Specials' : `Season ${s.seasonNumber}`,
        episodeCount: s.statistics?.totalEpisodeCount,
        // Already-added show: reflect current state (only previously-monitored
        // seasons start checked). Fresh add: default to everything but Specials.
        selected: alreadyAdded ? alreadyAdded.includes(s.seasonNumber) : s.seasonNumber !== 0,
        alreadyAdded: alreadyAdded?.includes(s.seasonNumber) ?? false
      }));
  }

  selectAll(): void {
    this.choices.forEach(c => (c.selected = true));
  }

  selectNone(): void {
    this.choices.forEach(c => (c.selected = false));
  }

  confirm(): void {
    const selected = this.choices.filter(c => c.selected).map(c => c.seasonNumber);
    this.dialogRef.close(selected);
  }

  cancel(): void {
    this.dialogRef.close(undefined);
  }
}
