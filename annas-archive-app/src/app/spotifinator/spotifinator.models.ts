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
}

export type SpotifyContentsAccess = 'Available' | 'Unavailable' | 'Forbidden';

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

export type SpotifyDuplicateConfidence = 'Exact' | 'Probable';

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

// ─── Command Types ───────────────────────────────────────────────────────────

/**
 * Mirrors the server-owned action enum in `SpotifyActionCatalog`. Read-only by
 * design: mutations are unreachable until the reviewed change-plan flow exists,
 * so there is deliberately no `create_playlist` here to render against.
 */
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
  | 'explain_capability'
  | 'unknown';

export type CommandData =
  | SpotifySearchResult
  | SpotifyPlaylist[]
  | SpotifyPlaylist
  | SpotifyPlaylistItemsPage
  | SpotifyRecentPlaylistContext[]
  | SpotifyLibraryAnalysis
  | SpotifyPlaylistOverlap
  | SpotifyTopItems
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
