export interface DropboxEpubFile {
  id: string;
  name: string;
  path: string;
  size: number;
  serverModified: string;
}

export interface DropboxEpubChapter {
  id: number;
  title: string;
  level: number;
  wordCount: number;
}

export interface DropboxEpubChaptersResponse {
  title: string;
  chapters: DropboxEpubChapter[];
}

export interface DropboxChapterContent {
  id: number;
  title: string;
  content: string;
  characterCount: number;
  wordCount: number;
}

export interface DropboxEpubStatus {
  cached: boolean;
  inProgress: boolean;
  chaptersTotal: number;
  chaptersCached: number;
  percent: number;
  cachedAt?: string;
}

export interface DropboxBookSearchResult {
  chapterId: number;
  title: string;
  matchCount: number;
  position: number;
  snippet: string;
}

export interface SummarizeResponse {
  summary: string;
}

export interface SummarizeRequestPayload {
  text: string;
  bookTitle?: string;
  author?: string;
  year?: number;
  premise?: string;
  dropboxPath?: string;
  chapterId?: number;
  wordOffset?: number;
  knownWords?: string[];
}

export interface FullChapterSummaryRequest {
  dropboxPath: string;
  chapterId: number;
  bookTitle?: string;
  author?: string;
  year?: number;
  premise?: string;
}

export interface FullChapterSummaryResponse {
  summary: string;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  allowanceUsedPercent?: number | null;
  tokensRemaining?: number | null;
  cachedAt: string;
}

export interface TokenUsageResponse {
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  allowance?: number | null;
  allowanceUsedPercent?: number | null;
  tokensRemaining?: number | null;
  resetsAtUtc?: string | null;
}

export interface LearnMoreRequestPayload {
  term: string;
  definition?: string;
  dropboxPath?: string;
  bookTitle?: string;
  context?: string;
}

export interface LearnMoreResponse {
  detail: string;
}

export interface FlashcardRequestPayload {
  term: string;
  definition?: string;
  dropboxPath?: string;
  bookTitle?: string;
  context?: string;
}

export interface FlashcardItem {
  term: string;
  definition: string;
  etymology: string;
  usageExamples: string[];
  notes?: string;
}

export interface FlashcardResult {
  cards: FlashcardItem[];
}

export interface WikiImagesResponse {
  images: string[];
}
