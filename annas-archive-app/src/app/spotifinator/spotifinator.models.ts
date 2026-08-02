// ─── API Response Types ──────────────────────────────────────────────────────

export interface SpotifyTrack {
  id: string;
  name: string;
  uri: string;
  durationMs: number;
  artists: string;
  albumName: string;
  albumArtUrl: string | null;
  spotifyUrl: string | null;
}

export interface SpotifySearchResult {
  tracks: SpotifyTrack[];
  total: number;
}

/**
 * `trackCount` is nullable, and that is load-bearing. Spotify omits the item
 * summary for playlists it will not let us read, so null means "unknown" — never
 * zero. Render `contentsAvailable === false` as "Contents unavailable"; showing 0
 * is how a followed playlist full of music looks identical to an empty one.
 */
export interface SpotifyPlaylist {
  id: string;
  name: string;
  imageUrl: string | null;
  trackCount: number | null;
  spotifyUrl: string | null;
  contentsAvailable: boolean;
  snapshotId: string | null;
  ownerId: string | null;
  ownerName: string | null;
  isOwnedByUser: boolean;
  isCollaborative: boolean;
  isPublic: boolean | null;
  uri: string | null;
  inventoryAt: string | null;
}

export type SpotifyContentsAccess = 'Available' | 'Unavailable' | 'Forbidden' | 'Partial';

export type SpotifyItemKind = 'Track' | 'Episode' | 'Local' | 'Unavailable';

export interface SpotifyPlaylistItem {
  position: number;
  kind: SpotifyItemKind;
  id: string | null;
  name: string | null;
  uri: string | null;
  artists: string;
  albumName: string | null;
  durationMs: number;
  spotifyUrl: string | null;
  isLocal: boolean;
  addedAt: string | null;
  isrc: string | null;
}

export interface SpotifyPlaylistItemsPage {
  playlistId: string;
  items: SpotifyPlaylistItem[];
  total: number;
  offset: number;
  limit: number;
  hasMore: boolean;
  access: SpotifyContentsAccess;
  snapshotId: string | null;
}

export interface SpotifyRecentPlaylistContext {
  playlistId: string;
  name: string | null;
  observedPlays: number;
  spotifyUrl: string | null;
}

export type SpotifyConnectionState =
  | 'Disconnected'
  | 'Connected'
  | 'ScopeLimited'
  | 'ReauthorizationRequired'
  | 'RateLimited'
  | 'QuotaExceeded'
  | 'SpotifyUnavailable';

export interface SpotifyConnectionStatus {
  state: SpotifyConnectionState;
  isConnected: boolean;
  accountId: string | null;
  spotifyUserId: string | null;
  displayName: string | null;
  grantedScopes: string[];
  missingScopes: string[];
  authorizedAt: string | null;
  reauthorizationDueAt: string | null;
  daysUntilReauthorization: number | null;
  lastSuccessfulCallAt: string | null;
  rateLimitedUntil: string | null;
  warning: string | null;
  lastError: string | null;
}

export interface SpotifyAuthorizeResponse {
  authorizationUrl: string;
}

// ─── Analysis ────────────────────────────────────────────────────────────────

export type SpotifyDuplicateConfidence = 'Exact' | 'Probable' | 'Recording';

export interface SpotifyDuplicateItemGroup {
  playlistId: string;
  playlistName: string;
  label: string;
  confidence: SpotifyDuplicateConfidence;
  positions: number[];
}

export interface SpotifyEmptyPlaylist {
  playlistId: string;
  name: string;
}

export interface SpotifyPlaylistOverlap {
  leftId: string;
  leftName: string;
  rightId: string;
  rightName: string;
  sharedItems: number;
  leftOnlyItems: number;
  rightOnlyItems: number;
  overlap: number;
  identical: boolean;
  supersetOf: string | null;
}

export interface SpotifyNamingCollision {
  normalized: string;
  playlists: SpotifyPlaylist[];
}

/**
 * `unreadable` is not decoration. Every other list here excludes those playlists,
 * so a cleanup decision taken without reading it is taken on a partial view.
 */
export interface SpotifyLibraryAnalysis {
  playlistsScanned: number;
  playlistsRead: number;
  unreadable: SpotifyPlaylist[];
  empty: SpotifyEmptyPlaylist[];
  duplicateItems: SpotifyDuplicateItemGroup[];
  overlappingPlaylists: SpotifyPlaylistOverlap[];
  namingCollisions: SpotifyNamingCollision[];
  recentlyObserved: SpotifyPlaylist[];
  usageUnknown: number;
  limitations: string[];
  generatedAt: string;
}

export interface SpotifyTopItem {
  id: string;
  name: string;
  detail: string | null;
  spotifyUrl: string | null;
  rank: number;
}

export interface SpotifyTopItems {
  kind: string;
  timeRange: string;
  items: SpotifyTopItem[];
}

export type SpotifyInventoryJobState =
  | 'NotStarted'
  | 'Queued'
  | 'Running'
  | 'Complete'
  | 'Partial'
  | 'Failed';

export interface SpotifyInventoryStatus {
  jobId: string | null;
  state: SpotifyInventoryJobState;
  totalPlaylists: number;
  processedPlaylists: number;
  readablePlaylists: number;
  partialPlaylists: number;
  unreadablePlaylists: number;
  startedAt: string | null;
  updatedAt: string | null;
  completedAt: string | null;
  lastInventoryAt: string | null;
  message: string | null;
}

export interface SpotifyKnownMusicIndex {
  artistKeys: string[];
  trackKeys: string[];
  playlistsIncluded: number;
  unreadablePlaylists: number;
  includesTopItems: boolean;
  includesRecentHistory: boolean;
  explicitOverrides: number;
}

export interface SpotifyKnownMusicReport {
  index: SpotifyKnownMusicIndex;
  coverage: string;
  generatedAt: string;
}

export type SpotifyDiscoveryDraftState = 'AwaitingClarification' | 'Resolving' | 'Ready' | 'Partial';
export type SpotifyCandidateResolution = 'Resolved' | 'Ambiguous' | 'NotFound';

export interface SpotifyDiscoveryCandidate {
  id: string;
  position: number;
  artist: string;
  title: string;
  rationale: string | null;
  resolution: SpotifyCandidateResolution;
  track: SpotifyTrack | null;
  alternatives: SpotifyTrack[];
  probablyUnfamiliar: boolean;
  familiarityLabel: string;
}

export interface SpotifyDiscoveryDraft {
  id: string;
  state: SpotifyDiscoveryDraftState;
  name: string;
  summary: string;
  userPrompts: string[];
  desiredTrackCount: number;
  clarifyingQuestion: string | null;
  candidates: SpotifyDiscoveryCandidate[];
  knownMusicCoverage: string;
  createdAt: string;
  updatedAt: string;
  savedAt: string | null;
}

export interface SpotifyDiscoveryDraftUpdate {
  name?: string;
  orderedCandidateIds?: string[];
  removeCandidateIds?: string[];
  candidateSelections?: Record<string, string>;
  saved?: boolean;
}

export interface SpotifyKnownMusicOverrideResult {
  kind: string;
  name: string;
  known: boolean;
  updatedAt: string;
}

// ─── Command Types ───────────────────────────────────────────────────────────

// ─── Change plans ────────────────────────────────────────────────────────────

/**
 * These mirror the server-owned types. Every action that changes anything is named
 * `plan_*` and returns a proposal, never a result: the confirm and cancel calls are
 * ordinary authenticated requests the user makes, not something the model can reach.
 */

export type SpotifyPlanStatus =
  | 'Draft' | 'AwaitingConfirmation' | 'Executing' | 'Completed'
  | 'PartiallyCompleted' | 'Failed' | 'Cancelled' | 'Expired' | 'Reverted';

export type SpotifyPlanSafetyTier = 'ReadOnly' | 'Additive' | 'Modifying' | 'HighImpact';

export type SpotifyPlanStepStatus = 'Pending' | 'Succeeded' | 'Failed' | 'Skipped';

export interface SpotifyPlanStep {
  ordinal: number;
  kind: string;
  playlistId: string | null;
  playlistName: string | null;
  uris: string[] | null;
  /** Only set on a VerifyPlaylistPopulated step. */
  expectedItemCount?: number | null;
  status: SpotifyPlanStepStatus;
  resultingSnapshotId: string | null;
  failure: string | null;
}

/**
 * What a half-finished plan leaves you able to do. Present only when a plan
 * actually stopped part-way — a completed plan has nothing to pick back up, and
 * offering to would invite re-running work that already landed.
 */
export interface SpotifyPlanRecovery {
  canResume: boolean;
  stepsSucceeded: number;
  stepsFailed: number;
  stepsNotAttempted: number;
  advice: string;
}

export interface SpotifyPlanTarget {
  playlistId: string;
  displayName: string;
  snapshotId: string | null;
}

/**
 * What the user reads before confirming. Computed server-side at build time from
 * the same data the steps came from, so the screen cannot disagree with the plan.
 */
export interface SpotifyPlanPreview {
  summary: string;
  confirmLabel: string;
  effects: string[];
  warnings: string[];
  requiresHighImpactAcknowledgement: boolean;
  itemsAdded: number;
  itemsRemoved: number;
  itemsSkippedAsDuplicates: number;
  itemsUnresolved: number;
  playlistsAffected: number;
}

export interface SpotifyPlan {
  id: string;
  action: string;
  safetyTier: SpotifyPlanSafetyTier;
  status: SpotifyPlanStatus;
  createdAtUtc: string;
  expiresAtUtc: string;
  targets: SpotifyPlanTarget[];
  preview: SpotifyPlanPreview;
  steps: SpotifyPlanStep[];
  originalRequest: string | null;
  confirmedBy: string | null;
  confirmedAtUtc: string | null;
  failure: string | null;
  canUndo: boolean;
  undoOfPlanId: string | null;
  recovery: SpotifyPlanRecovery | null;
}

export interface SpotifyAuditEvent {
  id: string;
  planId: string;
  kind: string;
  atUtc: string;
  applicationUser: string | null;
  spotifyAccountId: string | null;
  detail: string;
}

export type SpotifyAction =
  | 'search_tracks'
  | 'list_playlists'
  | 'find_playlists'
  | 'inspect_playlist'
  | 'list_playlist_items'
  | 'find_item_in_playlists'
  | 'analyze_playlist_library'
  | 'compare_playlists'
  | 'get_top_items'
  | 'get_recent_playlist_contexts'
  | 'get_known_music'
  | 'suggest_music'
  | 'refine_music_draft'
  | 'compare_draft_to_known_music'
  | 'plan_create_playlist'
  | 'plan_add_items'
  | 'plan_rename_playlist'
  | 'plan_remove_items'
  | 'plan_merge_playlists'
  | 'plan_remove_playlists_from_library'
  | 'explain_capability'
  | 'unknown';

export type CommandData =
  | SpotifyPlan
  | SpotifySearchResult
  | SpotifyPlaylist[]
  | SpotifyPlaylist
  | SpotifyPlaylistItemsPage
  | SpotifyRecentPlaylistContext[]
  | SpotifyLibraryAnalysis
  | SpotifyPlaylistOverlap
  | SpotifyTopItems
  | SpotifyInventoryStatus
  | SpotifyKnownMusicReport
  | SpotifyDiscoveryDraft
  | null;

export interface CommandResponse {
  action: SpotifyAction;
  confidence: number;
  message: string;
  data?: CommandData;
  clarification?: string | null;
}

// ─── Chat Message Types ──────────────────────────────────────────────────────

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  timestamp: Date;
  data?: CommandData;
  pending?: boolean;
  error?: boolean;
}

// ─── Component State ─────────────────────────────────────────────────────────

export type ViewState = 'idle' | 'processing' | 'error';
