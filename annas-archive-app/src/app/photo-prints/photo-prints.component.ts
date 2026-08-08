import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  PhotoPrintApiService, PhotoAsset, PrintSizeOption, PhotoPrintStatus, PrepareResult
} from '../services/photo-print-api.service';
import { LoggerService } from '../services/logger.service';

/** One chosen photo plus what to print of it. Held client-side until the order is placed. */
interface Selection {
  asset: PhotoAsset;
  sizeCode: string;
  quantity: number;
}

type DateRange = 'week' | 'month' | 'year' | 'all';

const DEFAULT_SIZE = '4x6';
const PAGE_SIZE = 100;

/**
 * Pick photos from the household Immich library and prepare a CVS pickup print
 * order. See DOCS/features/google-photos-cvs-print-automation-spec.md.
 *
 * The whole selection lives in memory until "Prepare order" — a run is only
 * created on the server at that point. Creating a draft run per click would
 * litter the database with abandoned runs every time someone browsed.
 */
@Component({
  selector: 'app-photo-prints',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatButtonModule, MatIconModule, MatSelectModule,
    MatFormFieldModule, MatInputModule, MatTooltipModule, MatProgressSpinnerModule,
    MatCheckboxModule
  ],
  templateUrl: './photo-prints.component.html',
  styleUrl: './photo-prints.component.scss'
})
export class PhotoPrintsComponent implements OnInit {
  status: PhotoPrintStatus | null = null;
  sizes: PrintSizeOption[] = [];

  photos: PhotoAsset[] = [];
  loadingPhotos = false;
  loadingMore = false;
  nextPage: number | null = null;
  totalPhotos = 0;

  dateRange: DateRange = 'month';
  favoritesOnly = false;

  /** Keyed by asset id so the grid can answer "is this chosen" in constant time. */
  selections = new Map<string, Selection>();

  preparing = false;
  result: PrepareResult | null = null;
  error: string | null = null;

  readonly ranges: { value: DateRange; label: string }[] = [
    { value: 'week', label: 'Last 7 days' },
    { value: 'month', label: 'Last 30 days' },
    { value: 'year', label: 'Last year' },
    { value: 'all', label: 'Everything' }
  ];

  constructor(
    private api: PhotoPrintApiService,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
    this.api.getStatus().subscribe({
      next: (status) => {
        this.status = status;
        if (status.configured && status.reachable) this.loadPhotos();
      },
      error: (err) => {
        this.logger.error('[PhotoPrints] status failed', err);
        this.error = 'Could not reach the photo print service.';
      }
    });

    this.api.getSizes().subscribe({
      next: (sizes) => (this.sizes = sizes),
      error: (err) => this.logger.error('[PhotoPrints] sizes failed', err)
    });
  }

  // ─── Browsing ────────────────────────────────────────────────────────

  private rangeStart(): string | undefined {
    if (this.dateRange === 'all') return undefined;
    const now = new Date();
    const days = this.dateRange === 'week' ? 7 : this.dateRange === 'month' ? 30 : 365;
    return new Date(now.getTime() - days * 86_400_000).toISOString();
  }

  loadPhotos(): void {
    this.loadingPhotos = true;
    this.error = null;
    this.api.browsePhotos({
      takenAfter: this.rangeStart(),
      favoritesOnly: this.favoritesOnly,
      page: 1,
      size: PAGE_SIZE
    }).subscribe({
      next: (page) => {
        this.photos = page.items;
        this.totalPhotos = page.total;
        this.nextPage = page.nextPage;
        this.loadingPhotos = false;
      },
      error: (err) => {
        this.logger.error('[PhotoPrints] browse failed', err);
        this.error = 'Could not load photos — is Immich running?';
        this.loadingPhotos = false;
      }
    });
  }

  loadMore(): void {
    if (this.nextPage === null || this.loadingMore) return;
    this.loadingMore = true;
    this.api.browsePhotos({
      takenAfter: this.rangeStart(),
      favoritesOnly: this.favoritesOnly,
      page: this.nextPage,
      size: PAGE_SIZE
    }).subscribe({
      next: (page) => {
        this.photos = [...this.photos, ...page.items];
        this.nextPage = page.nextPage;
        this.loadingMore = false;
      },
      error: (err) => {
        this.logger.error('[PhotoPrints] load more failed', err);
        this.loadingMore = false;
      }
    });
  }

  onFiltersChanged(): void {
    // Selections deliberately survive a filter change — someone picking a few
    // from last week and a few from last year should not lose the first lot.
    this.loadPhotos();
  }

  thumbnailUrl(asset: PhotoAsset): string {
    return this.api.thumbnailUrl(asset.id);
  }

  /** Keeps thumbnails from re-fetching when the grid grows via Load more. */
  trackByAssetId(_index: number, asset: PhotoAsset): string {
    return asset.id;
  }

  // ─── Selecting ───────────────────────────────────────────────────────

  isSelected(asset: PhotoAsset): boolean {
    return this.selections.has(asset.id);
  }

  toggle(asset: PhotoAsset): void {
    if (this.selections.has(asset.id)) {
      this.selections.delete(asset.id);
    } else {
      this.selections.set(asset.id, { asset, sizeCode: DEFAULT_SIZE, quantity: 1 });
    }
    this.result = null;
  }

  get selectedList(): Selection[] {
    return Array.from(this.selections.values());
  }

  get totalPrints(): number {
    // Quantity, not row count — this is what the order costs and what the
    // server's per-run ceiling is checked against.
    return this.selectedList.reduce((sum, s) => sum + s.quantity, 0);
  }

  get overLimit(): boolean {
    return !!this.status && this.totalPrints > this.status.maxPrintsPerRun;
  }

  setSize(selection: Selection, sizeCode: string): void {
    selection.sizeCode = sizeCode;
    this.result = null;
  }

  setQuantity(selection: Selection, raw: string | number): void {
    const value = Math.round(Number(raw));
    selection.quantity = Number.isFinite(value) ? Math.min(99, Math.max(1, value)) : 1;
    this.result = null;
  }

  remove(selection: Selection): void {
    this.selections.delete(selection.asset.id);
    this.result = null;
  }

  clearAll(): void {
    this.selections.clear();
    this.result = null;
  }

  /**
   * Rough check so a too-small photo is flagged before the order, not at the
   * counter. The server recomputes this properly during preparation — this is
   * only an early warning from the dimensions Immich already gave us.
   */
  looksLowResolution(selection: Selection): boolean {
    const size = this.sizes.find(s => s.code === selection.sizeCode);
    if (!size || !selection.asset.width || !selection.asset.height) return false;

    const landscape = selection.asset.width >= selection.asset.height;
    const printWide = size.isSquare ? size.shortInches : (landscape ? size.longInches : size.shortInches);
    const printTall = size.isSquare ? size.shortInches : (landscape ? size.shortInches : size.longInches);

    const dpi = Math.min(selection.asset.width / printWide, selection.asset.height / printTall);
    return dpi < 150;
  }

  // ─── Placing the order ───────────────────────────────────────────────

  prepareOrder(): void {
    if (!this.selectedList.length || this.preparing) return;

    this.preparing = true;
    this.error = null;
    this.result = null;

    this.api.createRun().subscribe({
      next: ({ runId }) => this.addItemsThenPrepare(runId),
      error: (err) => this.fail('Could not start the order.', err)
    });
  }

  /**
   * Items are added one at a time because the server validates each against the
   * per-run ceiling as it goes. Sequential rather than parallel so a rejection
   * lands on the item that caused it.
   */
  private addItemsThenPrepare(runId: string): void {
    const queue = [...this.selectedList];

    const next = (): void => {
      const selection = queue.shift();
      if (!selection) {
        this.api.prepare(runId).subscribe({
          next: (result) => {
            this.result = result;
            this.preparing = false;
          },
          error: (err) => this.fail(err?.error?.error ?? 'Could not prepare the prints.', err)
        });
        return;
      }

      this.api.addItem(
        runId, selection.asset.id, selection.asset.fileName,
        selection.sizeCode, selection.quantity
      ).subscribe({
        next: () => next(),
        error: (err) => this.fail(err?.error?.error ?? `Could not add ${selection.asset.fileName}.`, err)
      });
    };

    next();
  }

  private fail(message: string, err: unknown): void {
    this.logger.error('[PhotoPrints] order failed', err);
    this.error = message;
    this.preparing = false;
  }
}
