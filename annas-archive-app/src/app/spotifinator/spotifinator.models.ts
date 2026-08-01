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

export interface SpotifyPlaylist {
  id: string;
  name: string;
  imageUrl: string | null;
  trackCount: number;
  spotifyUrl: string | null;
  contentsAvailable: boolean;
  snapshotId: string | null;
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

// ─── Vibe Generation Types ───────────────────────────────────────────────────

export interface VibeGenerationResult {
  foundTracks: SpotifyTrack[];
  notFoundSongs: string[];
  createdPlaylist: SpotifyPlaylist | null;
}

// ─── AI Command Types ────────────────────────────────────────────────────────

export type SpotifyAction =
  | 'search_tracks'
  | 'list_playlists'
  | 'create_playlist'
  | 'delete_playlist'
  | 'add_to_playlist'
  | 'remove_from_playlist'
  | 'describe_playlist'
  | 'unknown';

export interface ParsedCommand {
  action: SpotifyAction;
  searchQuery?: string;
  playlistName?: string;
  playlistId?: string;
  trackUris?: string[];
  description?: string;
  confidence: number;
  clarificationNeeded?: string;
}

export interface CommandResponse {
  parsed: ParsedCommand;
  naturalResponse: string;
  data?: SpotifySearchResult | SpotifyPlaylist[] | SpotifyPlaylist | VibeGenerationResult | null;
  error?: string;
}

// ─── Chat Message Types ──────────────────────────────────────────────────────

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  timestamp: Date;
  data?: SpotifySearchResult | SpotifyPlaylist[] | SpotifyPlaylist | VibeGenerationResult | null;
  pending?: boolean;
  error?: boolean;
}

// ─── Component State ─────────────────────────────────────────────────────────

export type ViewState = 'idle' | 'processing' | 'error';
