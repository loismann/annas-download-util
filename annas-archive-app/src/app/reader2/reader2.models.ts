/**
 * The wire shapes of `/api/reader2`, mirroring `Reader2Contracts.cs`.
 *
 * Hand-written rather than generated, and kept in one file so a change to the
 * API is one diff here — Reader I spread its response shapes across a component,
 * two services, and several inline `any`s, which is how a renamed field became
 * `undefined` on screen instead of a compile error.
 */

/** A book type, as the picker sees it. Never carries prompt text. */
export interface Lens {
  key: string;
  displayName: string;
  description: string;
  icon: string;
  sortOrder: number;
  isDefault: boolean;
  buildsStoryModel: boolean;
  storyVocabulary: StoryVocabulary | null;
}

export interface StoryVocabulary {
  actors: string;
  groups: string;
  threads: string;
}

/** One shelf entry. `isAvailable` is false when the file has gone missing. */
export interface Book {
  bookId: string;
  fileName: string;
  title: string;
  authors: string[];
  lensKey: string;
  addedAtUtc: string;
  lastOpenedAtUtc: string | null;
  isAvailable: boolean;
}

export interface ChapterInfo {
  id: number;
  title: string;
  level: number;
  wordCount: number;

  /**
   * Whether a summary for this chapter is already stored under this book type.
   *
   * Served by the server rather than worked out here: an artifact is keyed by
   * lens and prompt version, so whether something has already been paid for is a
   * question only the store can answer.
   */
  hasSummary: boolean;

  /**
   * The summary exists but predates the current prompt.
   *
   * Separate from {@link hasSummary} on purpose: the chapter *is* summarised, and
   * a newer wording merely exists for whoever wants to pay to apply it. Folding
   * the two together is what used to make a prompt edit read as though the book
   * had never been summarised at all.
   */
  summaryIsStale: boolean;
}

export interface ChapterList {
  title: string;
  lensKey: string;
  chapters: ChapterInfo[];
}

export interface Chapter {
  chapter: ChapterInfo;
  text: string;
}

/** Where one summarisable section begins and ends, in words. */
export interface SectionInfo {
  index: number;
  startWord: number;
  wordCount: number;
}

/**
 * A passage the reader selected, with where it starts in the chapter.
 *
 * <p>Selecting text does not itself buy anything. It offers the reader two
 * explicit choices — explain it, or file the word — because a selection is easy
 * to make by accident while reading, and the rule the whole reader is built on
 * is that nothing is generated without being asked for.</p>
 */
export interface PassageSelection {
  text: string;
  wordOffset: number;
}

/** Everything the model writes for a reader is Markdown. */
export interface Prose {
  markdown: string;
}

export interface SearchHit {
  chapterId: number;
  chapterTitle: string;
  matchCount: number;
  snippet: string;
  firstWordOffset: number;
}

export interface ReadingPosition {
  chapter: number;
  wordOffset: number;
  updatedAtUtc: string;
}

/**
 * A place one reader marked. Carries no lens key: a mark is in the text, and the
 * text does not change when the book type does.
 */
export interface Bookmark {
  id: string;
  chapter: number;
  wordOffset: number;
  label: string | null;
  createdAtUtc: string;
}

export interface ReadingPreferences {
  fontFamily: 'serif' | 'sans' | 'mono';
  fontSize: number;
  theme: 'light' | 'dark' | 'sepia';
  splitRatio: number;
}

/**
 * What a reader who has never set anything gets.
 *
 * <p>Here rather than in the store, because the presenters need it too: a
 * component with an input of this shape has to default it to something, and
 * three copies of the same literal is three places to change when the default
 * changes — and two of them will be missed. Mirrors the server's own defaults in
 * `ReaderStateStore.ReadingPreferences`.</p>
 */
export const DEFAULT_PREFERENCES: ReadingPreferences = {
  fontFamily: 'serif',
  fontSize: 18,
  theme: 'light',
  splitRatio: 0.6
};

export type TermState = 'Known' | 'Studying';

export interface VocabularyTerm {
  term: string;
  termNorm: string;
  state: TermState;
  definition: string | null;
  firstSeenBookId: string | null;
  updatedAtUtc: string;
}

export interface Definition {
  term: string;
  meaning: string;
  norm: string;
}

export interface SectionVocabulary {
  terms: Definition[];
}

export interface DeepDive {
  html: string;
}

export interface Flashcard {
  term: string;
  definition: string;
  addedAtUtc: string;
  norm: string;
}

export interface Flashcards {
  cards: Flashcard[];
}

/** One step of a streamed operation. The same shape for every stream. */
export interface ProgressStep {
  stage: string;
  stepNumber: number;
  totalSteps: number;
  message: string;
}

// ─── the story model ──────────────────────────────────────────────────

export type ActorTier = 'Mentioned' | 'Minor' | 'Secondary' | 'Major';
export type ThreadStatus = 'Active' | 'Dormant' | 'Resolved' | 'Abandoned';

export interface ArcPoint {
  chapter: number;
  change: string;
}

export interface Actor {
  id: string;
  canonicalName: string;
  aliases: string[];
  tier: ActorTier;
  groupIds: string[];
  role: string;
  dossier: string;
  firstSeenChapter: number;
  lastSeenChapter: number;
  status: string;
  arc: ArcPoint[];

  /** The reader's own words. Written only by them, never by extraction. */
  readerNote: string;

  /**
   * Kept off the map, at the reader's word.
   *
   * Still in the cast list, marked — the extraction did find them in the book,
   * and a record that silently forgot people would be worth less than one that
   * is merely crowded.
   */
  hidden: boolean;
}

/** What the reader has corrected about one entry. Empty fields clear it. */
export interface ActorCorrection {
  preferredName: string | null;
  note: string | null;
  sameAs: string[];
}

export interface ActorGroup {
  id: string;
  name: string;
  kind: string;
  memberIds: string[];
  rivalGroupIds: string[];

  /** Filtered on by the server, exactly as an actor's is — a faction is a spoiler. */
  firstSeenChapter: number;
}

/** One chapter-tagged thing that passed between two actors. */
export interface EdgeNote {
  chapter: number;
  what: string;
}

export interface ActorEdge {
  from: string;
  to: string;
  type: string;
  sinceChapter: number;
  endedChapter: number | null;

  /**
   * How these two have got on, chapter by chapter and append-only. This is what
   * the map shows when a line between two people is clicked — one overwritten
   * string could not answer "how do these two know each other", because the
   * chapter that made them allies and the chapter that strained it are both
   * part of the answer.
   */
  notes: EdgeNote[];
}

export interface StoryThread {
  id: string;
  name: string;
  status: ThreadStatus;
  participantIds: string[];
  startedChapter: number;
  lastAdvancedChapter: number;
  beats: { chapter: number; whatMoved: string }[];
  relatedThreads: { threadId: string; relation: string }[];
  returnedInChapter: number | null;
  returnedAfterChapters: number | null;
}

/** Two entries that might be one person, waiting for the reader to say. */
export interface CandidateMerge {
  id: string;
  actorId: string;
  otherActorId: string | null;
  alias: string;
  reason: string;
  proposedInChapter: number;
}

/**
 * The cast, as far as the reader has read.
 *
 * `throughChapter` is echoed back by the server so the panel cannot render a
 * model against the wrong reading position, and `vocabulary` is what this book
 * type calls the three parts — the client holds no table of its own.
 */
/** What kind of place something is. Serialised by name, like every other enum. */
export type PlaceKind = 'Settlement' | 'Building' | 'Region' | 'Vessel' | 'Realm' | 'Other';

/**
 * Somewhere the book goes.
 *
 * <p>`partOf` is the id of the place this one sits inside — a room in a house, a
 * house in a city. Empty when nothing contains it, when the book has not said, or
 * when the container is still ahead of the reader.</p>
 */
export interface Place {
  id: string;
  name: string;
  aliases: string[];
  kind: PlaceKind;
  description: string;
  partOf: string;
  firstSeenChapter: number;
  lastSeenChapter: number;
}

export interface StoryModel {
  actors: Actor[];
  places: Place[];
  groups: ActorGroup[];
  edges: ActorEdge[];
  threads: StoryThread[];
  openQuestions: CandidateMerge[];
  chaptersIngested: number[];
  vocabulary: StoryVocabulary;
  throughChapter: number;
}
