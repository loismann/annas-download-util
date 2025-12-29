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

export interface ProcessingStep {
  stage: 'chunks' | 'sections' | 'final' | 'complete' | 'error';
  stepNumber: number;
  totalSteps: number;
  message: string;
  success: boolean;
  error?: string | null;
}

export interface FullChapterSummaryResponse {
  summary: string;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  allowanceUsedPercent?: number | null;
  tokensRemaining?: number | null;
  cachedAt: string;
  steps: ProcessingStep[];
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
  knownWords?: string[];
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

export interface ChunkBoundary {
  start: number;
  end: number;
  wordCount: number;
}

export interface ChunkBoundariesResponse {
  chapterId: number;
  chunks: ChunkBoundary[];
  cachedAt: string;
}

export interface SectionSummaryRequest {
  dropboxPath: string;
  chapterId: number;
  sectionIndex: number;
  bookTitle?: string;
  author?: string;
}

export interface SectionSummaryResponse {
  summary: string;
  sectionIndex: number;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  cachedAt: string;
  vocab?: FlashcardItem[] | null;
}

export interface CharacterNode {
  id: string;
  label: string;
  description: string;
  detailedDescription?: string;
}

export interface CharacterEdge {
  from: string;
  to: string;
  label: string;
  detailedDescription?: string;
}

export interface CharacterGraphResponse {
  nodes: CharacterNode[];
  edges: CharacterEdge[];
  summaryCount: number;
  cachedAt: string;
  currentSummaryCount?: number;
  needsUpdate?: boolean;
}

export interface CharacterGraphRequest {
  dropboxPath: string;
  bookTitle?: string;
  context?: string;
}

export interface CharacterGraphUpdateRequest {
  dropboxPath: string;
  newContent: string;
}
