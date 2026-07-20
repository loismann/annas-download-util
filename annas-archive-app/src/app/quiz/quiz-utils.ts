import { QuizMode, QuizOption, QuizQuestion, QuizQuestionType, QuizSessionQuestion, QuizSubject } from './quiz.models';

export type QuizAnswer = string | string[];

const DEFAULT_MODES: QuizMode[] = [
  {
    id: 'practice',
    label: 'Practice',
    questionCount: 10,
    shuffleQuestions: true,
    shuffleOptions: true,
    showFeedback: true,
    allowReview: true
  },
  {
    id: 'test',
    label: 'Test',
    questionCount: 25,
    shuffleQuestions: true,
    shuffleOptions: true,
    showFeedback: false,
    allowReview: false,
    timeLimitSeconds: 900
  }
];

export function getModes(subject: QuizSubject): QuizMode[] {
  return subject.modes && subject.modes.length > 0 ? subject.modes : DEFAULT_MODES;
}

export function getDefaultMode(): QuizMode {
  return DEFAULT_MODES[0];
}

export function getModeById(subject: QuizSubject, modeId?: string): QuizMode {
  const modes = getModes(subject);
  if (!modeId) return modes[0];
  return modes.find(mode => mode.id === modeId) ?? modes[0];
}

export function flattenQuestions(subject: QuizSubject): QuizQuestion[] {
  return subject.questionSets.flatMap(set => set.questions);
}

export function buildSessionQuestions(subject: QuizSubject, mode: QuizMode): QuizSessionQuestion[] {
  const baseQuestions = [...flattenQuestions(subject)];
  const shuffled = mode.shuffleQuestions ? shuffleArray(baseQuestions) : baseQuestions;
  const count = mode.questionCount && mode.questionCount > 0 ? mode.questionCount : shuffled.length;
  const selected = shuffled.slice(0, Math.min(count, shuffled.length));
  return selected.map(question => ({
    question,
    options: buildOptions(question, mode)
  }));
}

export function buildOptions(question: QuizQuestion, mode: QuizMode): QuizOption[] {
  const options = question.options ? [...question.options] : defaultOptionsForType(question.type);
  return mode.shuffleOptions ? shuffleArray(options) : options;
}

export function isMultiSelect(type: QuizQuestionType): boolean {
  return type === 'multi-select';
}

export function scoreAnswer(question: QuizQuestion, answer?: QuizAnswer): boolean {
  if (!answer) return false;
  if (question.type === 'short-answer') {
    const accepted = question.acceptedAnswers ?? [];
    const normalized = normalizeAnswer(Array.isArray(answer) ? answer.join(' ') : answer);
    return accepted.some(val => normalizeAnswer(val) === normalized);
  }

  const correct = (question.correctOptionIds ?? []).map(id => id.toLowerCase());
  if (correct.length === 0) return false;

  const selected = Array.isArray(answer) ? answer : [answer];
  const normalized = selected.map(id => id.toLowerCase());
  return setEquals(new Set(correct), new Set(normalized));
}

export function normalizeAnswer(value: string): string {
  return value.trim().toLowerCase().replace(/\s+/g, ' ');
}

export function formatTime(secondsRemaining: number): string {
  const minutes = Math.floor(secondsRemaining / 60);
  const seconds = secondsRemaining % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

function defaultOptionsForType(type: QuizQuestionType): QuizOption[] {
  if (type === 'true-false') {
    return [
      { id: 'true', text: 'True' },
      { id: 'false', text: 'False' }
    ];
  }
  return [];
}

function setEquals<T>(left: Set<T>, right: Set<T>): boolean {
  if (left.size !== right.size) return false;
  for (const item of left) {
    if (!right.has(item)) return false;
  }
  return true;
}

function shuffleArray<T>(items: T[]): T[] {
  const arr = [...items];
  for (let i = arr.length - 1; i > 0; i -= 1) {
    const j = Math.floor(Math.random() * (i + 1));
    [arr[i], arr[j]] = [arr[j], arr[i]];
  }
  return arr;
}
