import {
  SpotifyDiscoveryDraft,
  SpotifyDuplicateItemGroup,
  SpotifyInventoryStatus,
  SpotifyLibraryAnalysis,
  SpotifyPlan,
  SpotifyPlanStep,
  SpotifyPlaylist,
  SpotifyPlaylistItem,
  SpotifyPlaylistItemsPage,
  SpotifyPlaylistOverlap,
  SpotifyRecentPlaylistContext,
  SpotifySearchResult,
  SpotifyTopItems
} from './spotifinator.models';

/**
 * Everything the Spotifinator screen says about a value, decided from that value
 * alone.
 *
 * These lived on the component, where they were indistinguishable from the
 * methods that load, poll and write. Nothing here reads component state, so
 * nothing here needs a TestBed to exercise — and the component is left holding
 * only the things that actually have state to hold.
 *
 * A plain object rather than loose exports because Angular templates can only
 * reach component members: the component exposes this whole object once, and the
 * template calls `present.itemCountLabel(playlist)`.
 */
export const SpotifinatorPresentation = {

  // ─── Type guards ───────────────────────────────────────────────────────────
  //
  // The command endpoint answers with a union, and the transcript renders
  // whichever card fits. Each guard names the field that distinguishes its
  // shape from every other one, so the checks stay independent of ordering.

  isSearchResult(data: unknown): data is SpotifySearchResult {
    return !!data && typeof data === 'object' && 'tracks' in data
      && Array.isArray((data as SpotifySearchResult).tracks);
  },

  isPlaylistArray(data: unknown): data is SpotifyPlaylist[] {
    return Array.isArray(data) && data.length > 0 && 'contentsAvailable' in data[0];
  },

  isPlaylist(data: unknown): data is SpotifyPlaylist {
    return !!data && typeof data === 'object' && !Array.isArray(data) && 'contentsAvailable' in data;
  },

  isItemsPage(data: unknown): data is SpotifyPlaylistItemsPage {
    return !!data && typeof data === 'object' && !Array.isArray(data) && 'access' in data;
  },

  isRecentContexts(data: unknown): data is SpotifyRecentPlaylistContext[] {
    return Array.isArray(data) && data.length > 0 && 'observedPlays' in data[0];
  },

  isAnalysis(data: unknown): data is SpotifyLibraryAnalysis {
    return !!data && typeof data === 'object' && !Array.isArray(data) && 'playlistsScanned' in data;
  },

  isTopItems(data: unknown): data is SpotifyTopItems {
    return !!data && typeof data === 'object' && !Array.isArray(data) && 'timeRange' in data;
  },

  isInventoryStatus(data: unknown): data is SpotifyInventoryStatus {
    return !!data && typeof data === 'object' && !Array.isArray(data)
      && 'processedPlaylists' in data && 'state' in data;
  },

  isDiscoveryDraft(data: unknown): data is SpotifyDiscoveryDraft {
    return !!data && typeof data === 'object' && !Array.isArray(data)
      && 'candidates' in data && 'desiredTrackCount' in data && 'userPrompts' in data;
  },

  isPlan(data: unknown): data is SpotifyPlan {
    return !!data && typeof data === 'object' && !Array.isArray(data)
      && 'preview' in data && 'steps' in data;
  },

  // ─── Playlists and their contents ──────────────────────────────────────────

  /**
   * Never renders a number when the count is unknown. The whole point of
   * `trackCount: number | null` is that "0 items" and "Spotify won't tell me"
   * must not look the same.
   */
  itemCountLabel(playlist: SpotifyPlaylist): string {
    if (!playlist.contentsAvailable || playlist.trackCount === null) {
      return 'Contents unavailable';
    }
    return playlist.trackCount === 1 ? '1 item' : `${playlist.trackCount} items`;
  },

  ownershipLabel(playlist: SpotifyPlaylist): string {
    if (playlist.isOwnedByUser) return 'Yours';
    if (playlist.isCollaborative) return 'Collaborative';
    return playlist.ownerName ? `Followed · ${playlist.ownerName}` : 'Followed';
  },

  inventoryLabel(playlist: SpotifyPlaylist): string {
    return playlist.inventoryAt
      ? `Inventoried ${new Date(playlist.inventoryAt).toLocaleString()}`
      : 'Not inventoried yet';
  },

  itemIcon(item: SpotifyPlaylistItem): string {
    switch (item.kind) {
      case 'Episode': return 'podcasts';
      case 'Local': return 'sd_storage';
      case 'Unavailable': return 'help_outline';
      default: return 'music_note';
    }
  },

  itemMeta(item: SpotifyPlaylistItem): string {
    if (item.kind === 'Unavailable') {
      return 'This item is no longer on Spotify';
    }

    const parts = [item.artists, item.albumName].filter(Boolean);
    if (item.kind === 'Local') parts.push('local file');
    if (item.durationMs > 0) parts.push(SpotifinatorPresentation.formatDuration(item.durationMs));
    return parts.join(' · ');
  },

  formatDuration(ms: number): string {
    const minutes = Math.floor(ms / 60000);
    const seconds = Math.floor((ms % 60000) / 1000);
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
  },

  // ─── Library analysis ──────────────────────────────────────────────────────

  /**
   * Whether an analysis is safe to act on. False whenever any playlist could not
   * be read — the counts below it are then a floor, not a total, and the UI has to
   * say so before anyone treats a list of "empty" playlists as a delete list.
   */
  analysisIsComplete(analysis: SpotifyLibraryAnalysis): boolean {
    return analysis.unreadable.length === 0;
  },

  overlapLabel(overlap: SpotifyPlaylistOverlap): string {
    if (overlap.identical) return 'Identical';
    if (overlap.supersetOf) return 'One contains the other';
    return `${Math.round(overlap.overlap * 100)}% overlap`;
  },

  duplicateLabel(group: SpotifyDuplicateItemGroup): string {
    const where = `positions ${group.positions.map(p => p + 1).join(', ')}`;
    if (group.confidence === 'Exact') return `${group.label} — same Spotify item at ${where}`;
    if (group.confidence === 'Recording') return `${group.label} — same ISRC at ${where}`;
    return `${group.label} — possibly the same recording at ${where}`;
  },

  // ─── Connection and inventory ──────────────────────────────────────────────

  formatConnectionDate(value: string | null): string {
    return value ? new Date(value).toLocaleString() : 'Not yet';
  },

  inventoryProgress(status: SpotifyInventoryStatus): number {
    return status.totalPlaylists > 0
      ? Math.round(status.processedPlaylists * 100 / status.totalPlaylists)
      : 0;
  },

  inventoryIsRunning(status: SpotifyInventoryStatus | null): boolean {
    return status?.state === 'Queued' || status?.state === 'Running';
  },

  // ─── Discovery drafts ──────────────────────────────────────────────────────

  candidateResolutionLabel(candidate: SpotifyDiscoveryDraft['candidates'][number]): string {
    // Numeric values keep already-persisted Phase 5 drafts readable across the
    // deployment that changes the API contract to string enum names.
    switch (candidate.resolution as unknown) {
      case 'Resolved': case 0: return 'Matched in Spotify catalog';
      case 'Ambiguous': case 1: return 'Multiple Spotify catalog matches';
      case 'NotFound': case 2: return 'No confident Spotify catalog match';
      default: return 'Spotify catalog status unavailable';
    }
  },

  /** Candidates that actually matched a Spotify track — the only ones creatable. */
  resolvedCandidateCount(draft: SpotifyDiscoveryDraft): number {
    return draft.candidates.filter(c => c.resolution === 'Resolved' && c.track).length;
  },

  // ─── Change plans ──────────────────────────────────────────────────────────

  /** Only a plan still awaiting a decision can be acted on. */
  planIsPending(plan: SpotifyPlan): boolean {
    return plan.status === 'AwaitingConfirmation' || plan.status === 'Draft';
  },

  /** The receipt's icon. Deliberately three outcomes and no more — a row of status
   *  glyphs per step is what made the transcript unreadable. */
  planOutcomeIcon(plan: SpotifyPlan): string {
    switch (plan.status) {
      case 'Completed': return 'check_circle';
      case 'PartiallyCompleted': return 'error_outline';
      case 'Failed': return 'cancel';
      default: return 'info_outline';
    }
  },

  /** Whether the step list is worth offering at all. A plan that simply worked has
   *  nothing to explain, so it gets no expander to ignore. */
  planHasTrouble(plan: SpotifyPlan): boolean {
    return plan.status === 'PartiallyCompleted'
      || plan.status === 'Failed'
      || plan.steps.some(step => step.status === 'Failed' || step.status === 'Skipped');
  },

  planStatusLabel(plan: SpotifyPlan): string {
    switch (plan.status) {
      case 'Completed': return 'Done';
      case 'PartiallyCompleted': return 'Partly done';
      case 'Failed': return 'Failed';
      case 'Cancelled': return 'Cancelled';
      case 'Expired': return 'Expired — the playlist changed';
      case 'Reverted': return 'Undone';
      case 'Executing': return 'Running…';
      default: return 'Waiting for you';
    }
  },

  /** A friendlier name than the enum for each step in the progress list. */
  planStepLabel(step: SpotifyPlanStep): string {
    const name = step.playlistName ? ` — ${step.playlistName}` : '';

    switch (step.kind) {
      case 'CreatePlaylist': return `Create the playlist${name}`;
      case 'AddItems': return `Add tracks${name}`;
      case 'RemoveItems': return `Remove tracks${name}`;
      case 'ReplaceItems': return `Replace the contents${name}`;
      case 'ReorderItems': return `Reorder${name}`;
      case 'ChangeDetails': return `Update the details${name}`;
      case 'VerifyPlaylistPopulated': return `Check everything arrived${name}`;
      case 'RemoveFromLibrary': return `Remove from your library${name}`;
      case 'AddToLibrary': return `Put back in your library${name}`;
      default: return step.kind + name;
    }
  },

  /**
   * A receipt, not a report. The full effects list was already read in the dialog;
   * repeating it here is the wall of text that made the chat unusable.
   */
  describeOutcome(plan: SpotifyPlan): string {
    switch (plan.status) {
      case 'Completed':
        return plan.preview.summary;
      case 'PartiallyCompleted':
        return `Partly done — ${plan.failure}`;
      case 'Failed':
        return `Nothing changed. ${plan.failure}`;
      default:
        return SpotifinatorPresentation.planStatusLabel(plan);
    }
  },

  // ─── Playback ──────────────────────────────────────────────────────────────

  deviceIcon(type: string): string {
    switch (type.toLowerCase()) {
      case 'computer': return 'computer';
      case 'smartphone': return 'smartphone';
      case 'tablet': return 'tablet';
      case 'speaker': return 'speaker';
      case 'tv': case 'castvideo': return 'tv';
      case 'automobile': return 'directions_car';
      default: return 'devices';
    }
  }
};
