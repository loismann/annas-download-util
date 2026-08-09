import { ChangeDetectorRef, Component, HostListener, NgZone, OnInit, OnDestroy, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { ScrollingModule, CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { MatCardModule } from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { VideoLibraryApiService, VideoDto } from '../services/video-library-api.service';
import { VideoCardComponent } from '../components/video-card/video-card.component';
import { VideoSidebarComponent } from '../components/video-sidebar/video-sidebar.component';
import { VideoEditDialogComponent, VideoEditDialogData, VideoEditDialogResult } from '../components/video-edit-dialog/video-edit-dialog.component';
import { VideoPlayerDialogComponent, VideoPlayerDialogData } from '../components/video-player-dialog/video-player-dialog.component';
import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';
import { TileSizeControlsComponent } from '../components/shared/tile-size-controls/tile-size-controls.component';
import { TileSize, matchesSearchTerm } from '../shared/media-grid';

@Component({
  selector: 'app-video-library',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ScrollingModule,
    MatCardModule,
    MatProgressSpinnerModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatCheckboxModule,
    VideoCardComponent,
    VideoSidebarComponent,
    TileSizeControlsComponent,
  ],
  templateUrl: './video-library.component.html',
  styleUrl: './video-library.component.scss'
})
export class VideoLibraryComponent implements OnInit, OnDestroy {
  /**
   * Ends in-flight *reads* when the component goes.
   *
   * Reads only. Unsubscribing an HttpClient call aborts the underlying request,
   * so the three PATCHes on this page — the metadata save, the rating and the
   * bookmark — are deliberately left unguarded: they used to run through here,
   * which meant rating a video and immediately navigating away silently
   * cancelled the save the user had just watched the UI accept.
   */
  private destroy$ = new Subject<void>();

  placeholderUrl = '/assets/video-placeholder.jpg';
  loading = true;
  sorting = false;
  error: string | null = null;
  videos: VideoDto[] = [];
  displayVideos: VideoDto[] = [];
  searchTerm = '';
  selectedGenre = '';
  genres: string[] = [];
  filterBookmarked = false;
  minPersonalRating = 0;
  sortOrder: 'title' | 'channel' | 'duration' | 'rating' | 'date' = 'date';
  sortDirection: 'down' | 'up' = 'down';
  tileSize: TileSize = 'medium';
  sidebarCollapsed = false;
  bulkEditMode = false;
  selectedVideosForBulk = new Set<string>();

  private _cachedFilteredVideos: VideoDto[] | null = null;
  private _cachedSortedVideos: VideoDto[] | null = null;
  private _lastSortOrder: string | null = null;
  private _lastSortDirection: string | null = null;
  private cachedItemsPerRow = 4;

  @ViewChild(CdkVirtualScrollViewport) virtualScroll?: CdkVirtualScrollViewport;

  constructor(
    private videoApi: VideoLibraryApiService,
    private dialog: MatDialog,
    public authService: AuthService,
    private zone: NgZone,
    private cdr: ChangeDetectorRef,
    private logger: LoggerService,
    private router: Router
  ) {}

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  get rowHeight(): number {
    switch (this.tileSize) {
      case 'small': return 220;
      case 'large': return 340;
      default: return 280;
    }
  }

  private getItemsPerRow(): number {
    switch (this.tileSize) {
      case 'small': return 6;
      case 'large': return 3;
      default: return 4;
    }
  }

  recalculateLayout(): void {
    this.cachedItemsPerRow = this.getItemsPerRow();
    this.virtualScroll?.checkViewportSize();
  }

  @HostListener('window:resize')
  onWindowResize(): void {
    this.recalculateLayout();
  }

  get videoRows(): VideoDto[][] {
    const videos = this.filteredVideos;
    const perRow = this.cachedItemsPerRow;
    const rows: VideoDto[][] = [];
    for (let i = 0; i < videos.length; i += perRow) {
      rows.push(videos.slice(i, i + perRow));
    }
    return rows;
  }

  trackByRow(index: number, row: VideoDto[]): string {
    return row[0]?.fileName || `row-${index}`;
  }

  trackByFileName(index: number, video: VideoDto): string {
    return video.fileName || `${video.title}-${index}`;
  }

  ngOnInit(): void {
    if (window.innerWidth <= 768) {
      this.sidebarCollapsed = true;
    }

    this.logger.log('[video-library] Starting paginated load...');
    this.videoApi.getVideosPaginated(0, 100, 'date', true).pipe(takeUntil(this.destroy$)).subscribe({
      next: (response) => {
        this.logger.log('[video-library] Paginated response received', {
          videosCount: response?.videos?.length ?? 0,
          totalCount: response?.totalCount ?? 0
        });
        this.videos = response.videos ?? [];
        this.displayVideos = this.videos;
        this.genres = this.buildGenreList(this.videos);
        this.loading = false;
        setTimeout(() => this.recalculateLayout(), 0);

        if (response.totalCount > response.videos.length) {
          this.loadRemainingVideos(response.videos.length, response.totalCount);
        }
      },
      error: (err) => {
        this.logger.error('[video-library] paginated load failed, falling back', err);
        this.videoApi.getVideos().pipe(takeUntil(this.destroy$)).subscribe({
          next: (videos) => {
            this.videos = videos ?? [];
            this.genres = this.buildGenreList(this.videos);
            this.loading = false;
            this.recomputeDisplayVideosAsync();
            setTimeout(() => this.recalculateLayout(), 0);
          },
          error: (fallbackErr) => {
            this.logger.error('[video-library] fallback load also failed', fallbackErr);
            this.error = 'Failed to load video library.';
            this.loading = false;
          }
        });
      }
    });
  }

  private loadRemainingVideos(loaded: number, total: number): void {
    const batchSize = 500;
    let skip = loaded;
    const pendingVideos: VideoDto[] = [];

    this.zone.runOutsideAngular(() => {
      const loadNextBatch = () => {
        if (skip >= total) {
          this.zone.run(() => {
            this.finishBackgroundLoad(pendingVideos);
            this.logger.log('[video-library] All videos loaded', { total: this.videos.length });
          });
          return;
        }

        this.videoApi.getVideosPaginated(skip, batchSize, 'date', true)
          .pipe(takeUntil(this.destroy$))
          .subscribe({
            next: (response) => {
              const batch = response.videos ?? [];
              pendingVideos.push(...batch);
              skip += batch.length;
              // An empty batch advances `skip` by nothing, so recursing on it
              // would re-request the same page for as long as the server keeps
              // claiming a higher totalCount than it will hand over — an
              // unbounded request loop with no way for the user to stop it.
              if (batch.length === 0) {
                this.zone.run(() => this.finishBackgroundLoad(pendingVideos));
                return;
              }
              loadNextBatch();
            },
            error: (err) => {
              this.logger.error('[video-library] Background load failed', err);
              this.zone.run(() => this.finishBackgroundLoad(pendingVideos));
            }
          });
      };

      loadNextBatch();
    });
  }

  /**
   * Folds whatever the background load managed to fetch into the grid.
   *
   * Shared by all three ways that load can end — every batch in, a batch that
   * came back empty, or an outright failure — because a partly loaded library
   * on screen beats none, and because three copies of this had already started
   * to drift apart.
   */
  private finishBackgroundLoad(pending: VideoDto[]): void {
    if (pending.length === 0) return;

    this.videos = [...this.videos, ...pending];
    this._cachedFilteredVideos = null;
    this._cachedSortedVideos = null;
    this.genres = this.buildGenreList(this.videos);
    this.recomputeDisplayVideosAsync();
  }

  invalidateFilterCache(): void {
    this._cachedFilteredVideos = null;
    this._cachedSortedVideos = null;
    this.recomputeDisplayVideosAsync();
  }

  private invalidateSortCache(): void {
    this._cachedSortedVideos = null;
    this.recomputeDisplayVideosAsync();
  }

  private recomputeDisplayVideosAsync(): void {
    this.sorting = true;
    const filtered = this._cachedFilteredVideos ?? this.filterVideosInternal();
    this._cachedFilteredVideos = filtered;

    const sorted = this.sortVideos(filtered);
    this._cachedSortedVideos = sorted;
    this._lastSortOrder = this.sortOrder;
    this._lastSortDirection = this.sortDirection;

    this.displayVideos = sorted;
    this.sorting = false;
    this.cdr.detectChanges();
  }

  private filterVideosInternal(): VideoDto[] {
    const genre = this.selectedGenre.toLowerCase();

    return this.videos.filter(video => {
      if (genre) {
        const primary = video.primaryGenre?.toLowerCase() ?? '';
        const tags = (video.tags ?? []).map(tag => tag.toLowerCase());
        if (primary !== genre && !tags.includes(genre)) {
          return false;
        }
      }

      if (!matchesSearchTerm(
        this.searchTerm, video.title, video.channel, video.primaryGenre, ...(video.tags ?? [])
      )) {
        return false;
      }

      if (this.minPersonalRating > 0) {
        const personal = video.personalRating ?? 0;
        if (personal < this.minPersonalRating) return false;
      }

      if (this.filterBookmarked) {
        if (!video.bookmarked) return false;
      }

      return true;
    });
  }

  get filteredVideos(): VideoDto[] {
    return this.displayVideos;
  }

  private sortVideos(videos: VideoDto[]): VideoDto[] {
    if (videos.length === 0) return videos;

    type SortableVideo = { video: VideoDto; key: string | number };
    let sortable: SortableVideo[];

    switch (this.sortOrder) {
      case 'title':
        sortable = videos.map(video => ({
          video,
          key: (video.title || '').toLowerCase()
        }));
        sortable.sort((a, b) => this.applyDirection(
          (a.key as string).localeCompare(b.key as string)
        ));
        break;

      case 'channel':
        sortable = videos.map(video => ({
          video,
          key: (video.channel || '').toLowerCase()
        }));
        sortable.sort((a, b) => this.applyDirection(
          (a.key as string).localeCompare(b.key as string)
        ));
        break;

      case 'duration':
        sortable = videos.map(video => ({
          video,
          key: video.durationSeconds ?? 0
        }));
        sortable.sort((a, b) => this.applyDirection((a.key as number) - (b.key as number)));
        break;

      case 'rating':
        sortable = videos.map(video => ({
          video,
          key: video.personalRating ?? 0
        }));
        sortable.sort((a, b) => {
          if (a.key !== b.key) return this.applyDirection((a.key as number) - (b.key as number));
          return (a.video.title || '').localeCompare(b.video.title || '');
        });
        break;

      case 'date':
      default:
        sortable = videos.map(video => ({
          video,
          key: video.downloadedAt ? new Date(video.downloadedAt).getTime() : 0
        }));
        sortable.sort((a, b) => this.applyDirection((a.key as number) - (b.key as number)));
        break;
    }

    return sortable.map(s => s.video);
  }

  private applyDirection(value: number): number {
    const isAlphaSort = this.sortOrder === 'title' || this.sortOrder === 'channel';
    const multiplier = isAlphaSort
      ? (this.sortDirection === 'down' ? 1 : -1)
      : (this.sortDirection === 'down' ? -1 : 1);
    return value * multiplier;
  }

  onSortChange(preserveDirection = false): void {
    this.invalidateSortCache();
    if (!preserveDirection) {
      this.sortDirection = 'down';
    }
    this.virtualScroll?.scrollToIndex(0);
  }

  toggleSortDirection(): void {
    this.sortDirection = this.sortDirection === 'down' ? 'up' : 'down';
    this.onSortChange(true);
  }

  setTileSize(size: 'small' | 'medium' | 'large'): void {
    this.tileSize = size;
    setTimeout(() => this.recalculateLayout(), 0);
  }

  toggleBookmarkFilter(): void {
    this.filterBookmarked = !this.filterBookmarked;
    this.invalidateFilterCache();
  }

  toggleSidebar(): void {
    this.sidebarCollapsed = !this.sidebarCollapsed;
  }

  resetView(): void {
    this.searchTerm = '';
    this.selectedGenre = '';
    this.minPersonalRating = 0;
    this.sortOrder = 'date';
    this.sortDirection = 'down';
    this.tileSize = 'medium';
    this.filterBookmarked = false;
    this.bulkEditMode = false;
    this.selectedVideosForBulk.clear();
    // After the fields, not before them: recomputing first rebuilt the grid
    // from the filters that were about to be cleared, so Reset restored the
    // controls but left the same filtered set on screen.
    this.invalidateFilterCache();
    this.virtualScroll?.scrollToIndex(0);
    setTimeout(() => this.recalculateLayout(), 0);
  }

  private buildGenreList(videos: VideoDto[]): string[] {
    const set = new Set<string>();
    videos.forEach(video => {
      if (video.primaryGenre) {
        set.add(video.primaryGenre);
      }
      (video.tags ?? []).forEach(tag => {
        set.add(tag);
      });
    });
    return Array.from(set).sort((a, b) => a.localeCompare(b));
  }

  onThumbnailClick(video: VideoDto): void {
    this.openEditDialog(video);
  }

  openEditDialog(video: VideoDto): void {
    const dialogData: VideoEditDialogData = {
      fileName: video.fileName,
      title: video.title,
      channel: video.channel,
      duration: video.duration,
      resolution: video.resolution,
      format: video.format,
      fileSize: video.fileSize,
      thumbnailUrl: video.thumbnailUrl,
      description: video.description,
      primaryGenre: video.primaryGenre,
      tags: video.tags ?? [],
      playlist: video.playlist,
      youTubeId: video.youTubeId,
      availableGenres: this.genres
    };

    const dialogRef = this.dialog.open(VideoEditDialogComponent, {
      width: '920px',
      maxWidth: '95vw',
      data: dialogData,
      panelClass: 'video-edit-dialog-panel'
    });

    dialogRef.afterClosed().pipe(takeUntil(this.destroy$)).subscribe((result: VideoEditDialogResult | undefined) => {
      if (result?.deleted) {
        this.videos = this.videos.filter(v => v.fileName !== video.fileName);
        this._cachedFilteredVideos = null;
        this._cachedSortedVideos = null;
        this.genres = this.buildGenreList(this.videos);
        this.recomputeDisplayVideosAsync();
        return;
      }

      if (result) {
        const idx = this.videos.findIndex(v => v.fileName === video.fileName);
        if (idx >= 0) {
          this.videos[idx] = {
            ...this.videos[idx],
            title: result.title ?? video.title,
            channel: result.channel ?? video.channel,
            primaryGenre: result.primaryGenre ?? video.primaryGenre,
            tags: result.tags ?? video.tags,
            playlist: result.playlist ?? video.playlist
          };
          this._cachedFilteredVideos = null;
          this._cachedSortedVideos = null;
          this.genres = this.buildGenreList(this.videos);
          this.recomputeDisplayVideosAsync();
        }

        this.videoApi.updateVideoMetadata(video.fileName, {
          primaryGenre: result.primaryGenre ?? video.primaryGenre ?? null,
          tags: result.tags ?? video.tags ?? [],
          playlist: result.playlist ?? video.playlist ?? null,
          title: result.title ?? video.title,
          channel: result.channel ?? video.channel
        // Not guarded by destroy$ — see the note on that field. This is a PATCH.
        }).subscribe({
          next: () => this.logger.log('[video-library] Updated video metadata:', video.fileName),
          error: (err) => this.logger.error('[video-library] Failed to update video metadata:', err)
        });
      }
    });
  }

  onRatingChange(event: { video: VideoDto; rating: number }): void {
    const video = event.video;
    const current = video.personalRating ?? 0;
    const nextRating = event.rating === 1 && current === 1 ? 0 : event.rating;
    if (nextRating === current) return;

    video.personalRating = nextRating;
    // Not guarded by destroy$ — see the note on that field. This is a PATCH.
    this.videoApi.updateVideoRatings(video.fileName, {
      personalRating: nextRating
    }).subscribe({
      next: () => this.logger.log('[video-library] Updated personal rating:', video.fileName, nextRating),
      error: (err) => {
        this.logger.error('[video-library] Failed to update personal rating:', err);
        // Put the stars back, the way the bookmark toggle below already does.
        // Leaving the new rating on screen tells the user it saved.
        video.personalRating = current;
      }
    });
  }

  onBookmarkToggle(video: VideoDto): void {
    const newValue = !video.bookmarked;
    video.bookmarked = newValue;

    // Not guarded by destroy$ — see the note on that field. This is a PATCH.
    this.videoApi.updateVideoRatings(video.fileName, {
      bookmarked: newValue
    }).subscribe({
      next: () => this.logger.log('[video-library] Updated bookmark:', video.fileName, newValue),
      error: (err) => {
        this.logger.error('[video-library] Failed to update bookmark:', err);
        video.bookmarked = !newValue;
      }
    });
  }

  onEditClick(video: VideoDto): void {
    this.openEditDialog(video);
  }

  onPlayClick(video: VideoDto): void {
    const streamUrl = this.videoApi.getVideoStreamUrl(video.fileName);

    const dialogData: VideoPlayerDialogData = {
      title: video.title,
      channel: video.channel,
      streamUrl,
      youTubeId: video.youTubeId
    };

    this.dialog.open(VideoPlayerDialogComponent, {
      width: '90vw',
      maxWidth: '1400px',
      maxHeight: '90vh',
      data: dialogData,
      panelClass: 'video-player-dialog-panel'
    });
  }

  toggleBulkEditMode(): void {
    this.bulkEditMode = !this.bulkEditMode;
    if (!this.bulkEditMode) {
      this.selectedVideosForBulk.clear();
    }
  }

  toggleVideoSelection(video: VideoDto): void {
    if (!this.bulkEditMode) return;
    if (this.selectedVideosForBulk.has(video.fileName)) {
      this.selectedVideosForBulk.delete(video.fileName);
    } else {
      this.selectedVideosForBulk.add(video.fileName);
    }
  }

  selectAllVisible(): void {
    if (!this.bulkEditMode) return;
    for (const video of this.filteredVideos) {
      this.selectedVideosForBulk.add(video.fileName);
    }
  }
}
