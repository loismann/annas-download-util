import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { FormsModule } from '@angular/forms';
import { HOUSEHOLD_OWNERS } from '../../constants/owners';
import {
  MediaSearchApiService,
  BulkImportMovieRow,
  BulkImportMovieResult
} from '../../services/media-search-api.service';

interface ParsedRow extends BulkImportMovieRow {
  /** null when valid — the reason it can't be submitted, shown inline. */
  error: string | null;
}

type Stage = 'upload' | 'preview' | 'importing' | 'results';

/**
 * Bulk-adds movies to Radarr from a structured list (title/year to find the
 * movie, genres/owner to classify it once matched) — the batch equivalent of
 * searching + adding one at a time on the media-search page, for someone
 * with an existing list rather than typing titles in one by one.
 *
 * Parsing happens client-side (CSV, quoted-field aware) purely so a bad row
 * can be flagged before spending a network round trip on it; the actual
 * title+year matching against Radarr's TMDB-backed lookup happens
 * server-side (MediaRequestEndpoints.HandleMovieBulkImport) — this dialog
 * never guesses at ambiguous matches itself, it just displays whatever the
 * backend decided per row.
 */
@Component({
  selector: 'app-bulk-import-movies-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatButtonModule,
    MatIconModule, MatProgressSpinnerModule, MatCheckboxModule
  ],
  template: `
    <div class="bulk-import-dialog">
      <h2 mat-dialog-title>Bulk import movies</h2>

      <div mat-dialog-content>
        <ng-container [ngSwitch]="stage">
          <div *ngSwitchCase="'upload'" class="upload-stage">
            <p>
              Upload a CSV with columns <code>title, year, owner, genre</code> — then as many more genre
              columns as you want (a header row is optional). <code>owner</code>, if set, must be one of
              {{ ownerNames }}.
            </p>
            <p class="example">
              Example:<br />
              <code>The Street Fighter, 1974, Paul, Date Night, Kung Fu</code><br />
              → genres <code>Date Night</code> and <code>Kung Fu</code>
            </p>

            <input type="file" accept=".csv,text/csv" (change)="onFileSelected($event)" #fileInput />

            <p *ngIf="parseError" class="error">{{ parseError }}</p>
          </div>

          <div *ngSwitchCase="'preview'" class="preview-stage">
            <p>
              {{ validCount }} of {{ rows.length }} row(s) ready to import.
              <span *ngIf="invalidCount > 0" class="error">{{ invalidCount }} row(s) have a problem and will be skipped.</span>
            </p>
            <mat-checkbox [(ngModel)]="dateNightPool" name="dateNightPool" class="pool-toggle">
              Add to the Date Night pool
            </mat-checkbox>
            <p class="pool-hint">
              Pool movies are added as records only — nothing downloads until a date night is
              scheduled for it. Leave this off to acquire everything on the list immediately.
            </p>

            <div class="preview-table-wrap">
              <table class="preview-table">
                <thead>
                  <tr>
                    <th>Title</th>
                    <th>Year</th>
                    <th>Owner</th>
                    <th>Genres</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  <tr *ngFor="let row of rows" [class.invalid-row]="row.error">
                    <td>{{ row.title || '—' }}</td>
                    <td>{{ row.year ?? '—' }}</td>
                    <td>{{ row.owner ?? '—' }}</td>
                    <td>{{ row.genres.join(', ') || '—' }}</td>
                    <td class="row-status">
                      <mat-icon *ngIf="!row.error" class="ok-icon">check_circle</mat-icon>
                      <span *ngIf="row.error" class="error" [title]="row.error">{{ row.error }}</span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>

          <div *ngSwitchCase="'importing'" class="importing-stage">
            <mat-spinner diameter="32"></mat-spinner>
            <p>Matching against Radarr and adding — {{ results.length }} of {{ submittedCount }} done…</p>
          </div>

          <div *ngSwitchCase="'results'" class="results-stage">
            <p>
              {{ addedCount }} added, {{ existedCount }} already in Radarr, {{ failedCount }} skipped.
            </p>
            <div class="preview-table-wrap">
              <table class="preview-table">
                <thead>
                  <tr>
                    <th>Title</th>
                    <th>Year</th>
                    <th>Result</th>
                  </tr>
                </thead>
                <tbody>
                  <tr *ngFor="let r of results" [class.invalid-row]="r.status !== 'added' && r.status !== 'already-existed'">
                    <td>{{ r.title }}</td>
                    <td>{{ r.year ?? '—' }}</td>
                    <td>
                      <span [ngSwitch]="r.status">
                        <span *ngSwitchCase="'added'" class="ok-text">Added</span>
                        <span *ngSwitchCase="'already-existed'" class="ok-text">Already in Radarr</span>
                        <span *ngSwitchDefault class="error">{{ r.message || r.status }}</span>
                      </span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
        </ng-container>
      </div>

      <div mat-dialog-actions align="end">
        <button mat-stroked-button (click)="onClose()" *ngIf="stage !== 'importing'">
          {{ stage === 'results' ? 'Close' : 'Cancel' }}
        </button>
        <button mat-raised-button color="primary" *ngIf="stage === 'preview'" [disabled]="validCount === 0" (click)="onImport()">
          Import {{ validCount }} movie{{ validCount === 1 ? '' : 's' }}
        </button>
      </div>
    </div>
  `,
  styles: [`
    .bulk-import-dialog { min-width: min(640px, calc(100vw - 80px)); }
    .example { font-size: 0.9em; opacity: 0.8; }
    .error { color: var(--mat-sys-error, #d32f2f); }
    .ok-icon { color: var(--mat-sys-primary, #2e7d32); vertical-align: middle; }
    .ok-text { color: var(--mat-sys-primary, #2e7d32); }
    .pool-toggle { display: block; margin-top: 8px; }
    .pool-hint { font-size: 0.85em; opacity: 0.75; margin: 4px 0 0 32px; }
    .preview-table-wrap { max-height: 360px; overflow: auto; margin-top: 8px; }
    .preview-table { width: 100%; border-collapse: collapse; font-size: 0.9em; }
    .preview-table th, .preview-table td { text-align: left; padding: 4px 8px; border-bottom: 1px solid rgba(128,128,128,0.2); }
    .invalid-row { opacity: 0.85; }
    .importing-stage { display: flex; flex-direction: column; align-items: center; gap: 16px; padding: 24px 0; }
  `]
})
export class BulkImportMoviesDialogComponent {
  /** Rows per request — small enough that a chunk finishes well inside any
   * proxy/gateway timeout, large enough not to spam the API rate limiter. */
  private static readonly CHUNK_SIZE = 20;

  stage: Stage = 'upload';
  /** How many rows this import set out to do — the denominator of the progress count. */
  submittedCount = 0;
  parseError: string | null = null;
  rows: ParsedRow[] = [];
  results: BulkImportMovieResult[] = [];
  /** Catalog-only import: nothing downloads until a date night is scheduled. */
  dateNightPool = false;
  readonly ownerNames = HOUSEHOLD_OWNERS.join(', ');

  constructor(
    public dialogRef: MatDialogRef<BulkImportMoviesDialogComponent>,
    private mediaApi: MediaSearchApiService
  ) {}

  get validCount(): number {
    return this.rows.filter(r => !r.error).length;
  }

  get invalidCount(): number {
    return this.rows.length - this.validCount;
  }

  get addedCount(): number {
    return this.results.filter(r => r.status === 'added').length;
  }

  get existedCount(): number {
    return this.results.filter(r => r.status === 'already-existed').length;
  }

  get failedCount(): number {
    return this.results.length - this.addedCount - this.existedCount;
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.parseError = null;
    file.text().then(text => {
      try {
        this.rows = this.parseRows(text);
        if (this.rows.length === 0) {
          this.parseError = 'No rows found in that file.';
          return;
        }
        this.stage = 'preview';
      } catch (err) {
        this.parseError = err instanceof Error ? err.message : 'Could not parse that file.';
      }
    });
  }

  private parseRows(text: string): ParsedRow[] {
    const records = parseCsv(text).filter(r => r.some(cell => cell.trim().length > 0));
    if (records.length === 0) return [];

    // A header row's first cell will read "title" — skip it if present,
    // otherwise treat every record as data.
    const first = records[0][0]?.trim().toLowerCase();
    const dataRecords = first === 'title' ? records.slice(1) : records;

    return dataRecords.map(cols => {
      const title = (cols[0] ?? '').trim();
      const yearRaw = (cols[1] ?? '').trim();
      const owner = (cols[2] ?? '').trim() || null;
      // Everything after owner is its own genre column — as many as the row
      // has, not a fixed count (e.g. "Date Night, Kung Fu" as two columns,
      // not one semicolon-joined cell).
      const genres = cols
        .slice(3)
        .map(g => g.trim())
        .filter(g => g.length > 0);

      let year: number | null = null;
      let error: string | null = null;

      if (!title) {
        error = 'Title is required';
      } else if (yearRaw) {
        const parsed = parseInt(yearRaw, 10);
        if (Number.isNaN(parsed) || yearRaw.length !== 4) {
          error = `Invalid year "${yearRaw}"`;
        } else {
          year = parsed;
        }
      }

      if (!error && owner && !HOUSEHOLD_OWNERS.some(o => o.toLowerCase() === owner.toLowerCase())) {
        error = `Owner must be one of ${this.ownerNames}`;
      }

      return { title, year, genres, owner, error };
    });
  }

  /**
   * Submits in small chunks rather than one request for the whole list. Each row
   * costs a TMDB-backed Radarr lookup plus an add, so a few hundred rows is several
   * minutes of server work — well past what a single HTTP request survives. Chunking
   * also means a failure part-way through keeps the rows that already succeeded, and
   * gives an honest progress count instead of an indeterminate spinner.
   */
  onImport(): void {
    const validRows = this.rows.filter(r => !r.error).map(({ error, ...row }) => row);
    this.results = [];
    this.submittedCount = validRows.length;
    this.stage = 'importing';
    this.importChunk(validRows, 0);
  }

  private importChunk(rows: BulkImportMovieRow[], start: number): void {
    if (start >= rows.length) {
      this.stage = 'results';
      return;
    }

    const chunk = rows.slice(start, start + BulkImportMoviesDialogComponent.CHUNK_SIZE);
    this.mediaApi.bulkImportMovies(chunk, this.dateNightPool).subscribe({
      next: results => {
        this.results = [...this.results, ...results];
        this.importChunk(rows, start + chunk.length);
      },
      error: () => {
        // Keep whatever already imported — re-running the same file later is safe,
        // since rows that landed come back as "already in Radarr" rather than
        // duplicating.
        if (this.results.length > 0) {
          this.stage = 'results';
        } else {
          this.parseError = 'Import failed — please try again.';
          this.stage = 'preview';
        }
      }
    });
  }

  onClose(): void {
    this.dialogRef.close(this.stage === 'results');
  }
}

/** Minimal quoted-field-aware CSV parser — handles embedded commas/quotes
 * ("He said ""hi"", then left") without pulling in a dependency for four
 * simple columns. */
function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let field = '';
  let row: string[] = [];
  let inQuotes = false;

  for (let i = 0; i < text.length; i++) {
    const c = text[i];

    if (inQuotes) {
      if (c === '"') {
        if (text[i + 1] === '"') {
          field += '"';
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        field += c;
      }
      continue;
    }

    if (c === '"') {
      inQuotes = true;
    } else if (c === ',') {
      row.push(field);
      field = '';
    } else if (c === '\n' || c === '\r') {
      if (c === '\r' && text[i + 1] === '\n') i++;
      row.push(field);
      rows.push(row);
      row = [];
      field = '';
    } else {
      field += c;
    }
  }

  if (field.length > 0 || row.length > 0) {
    row.push(field);
    rows.push(row);
  }

  return rows;
}
