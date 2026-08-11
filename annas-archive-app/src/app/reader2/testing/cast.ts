import {
  Actor, ActorEdge, ActorGroup, ActorTier, CandidateMerge, ChapterInfo, StoryModel, StoryThread,
  ThreadStatus
} from '../reader2.models';

/**
 * Builders for story-model specs, so each test states only what it is about.
 * Spec-support only — nothing under `testing/` is imported by the app.
 */

export function actor(id: string, name: string, tier: ActorTier = 'Secondary', extra?: Partial<Actor>): Actor {
  return {
    id, canonicalName: name, aliases: [], tier, groupIds: [], role: '', dossier: '',
    firstSeenChapter: 0, lastSeenChapter: 0, status: '', arc: [], readerNote: '', hidden: false,
    ...extra
  };
}

export function edge(from: string, to: string, type: string, extra?: Partial<ActorEdge>): ActorEdge {
  return { from, to, type, sinceChapter: 0, endedChapter: null, notes: [], ...extra };
}

export function group(id: string, name: string, memberIds: string[] = []): ActorGroup {
  return { id, name, kind: 'Family', memberIds, rivalGroupIds: [], firstSeenChapter: 0 };
}

export function thread(
  id: string, name: string, status: ThreadStatus = 'Active', extra?: Partial<StoryThread>
): StoryThread {
  return {
    id, name, status, participantIds: [], startedChapter: 0, lastAdvancedChapter: 0,
    beats: [], relatedThreads: [], returnedInChapter: null, returnedAfterChapters: null, ...extra
  };
}

/**
 * A contents list, indexed the way the story model indexes chapters — by
 * position in the spine, front matter included. That offset is the whole reason
 * chapters are named rather than counted, so specs that care want a list where
 * the titles and the indices deliberately disagree.
 */
export function contents(...titles: string[]): ChapterInfo[] {
  return titles.map((title, id) => ({ id, title, level: 0, wordCount: 100, hasSummary: false, summaryIsStale: false }));
}

export function question(id: string, actorId: string, alias: string, otherActorId: string | null = null): CandidateMerge {
  return { id, actorId, otherActorId, alias, reason: 'The extraction was not certain.', proposedInChapter: 1 };
}

export function model(extra?: Partial<StoryModel>): StoryModel {
  return {
    actors: [], places: [], groups: [], edges: [], threads: [], openQuestions: [],
    chaptersIngested: [0],
    vocabulary: { actors: 'Characters', groups: 'Factions', threads: 'Plot threads' },
    throughChapter: 5, ...extra
  };
}
