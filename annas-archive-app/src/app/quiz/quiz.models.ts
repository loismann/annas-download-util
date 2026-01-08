export interface QuizIndex {
  version: number;
  updatedAt: string;
  subjects: QuizSubjectSummary[];
}

export interface QuizSubjectSummary {
  id: string;
  title: string;
  description?: string;
  questionCount: number;
  defaultModeId?: string;
  tags?: string[];
}

export interface QuizSubject {
  id: string;
  title: string;
  description?: string;
  version: number;
  updatedAt: string;
  defaultModeId?: string;
  modes?: QuizMode[];
  questionSets: QuizQuestionSet[];
}

export interface QuizMode {
  id: string;
  label: string;
  questionCount?: number;
  shuffleQuestions?: boolean;
  shuffleOptions?: boolean;
  timeLimitSeconds?: number;
  showFeedback?: boolean;
  allowReview?: boolean;
}

export interface QuizQuestionSet {
  id: string;
  title: string;
  description?: string;
  questions: QuizQuestion[];
}

export type QuizQuestionType =
  | 'multiple-choice'
  | 'multi-select'
  | 'true-false'
  | 'short-answer';

export interface QuizQuestion {
  id: string;
  type: QuizQuestionType;
  prompt: string;
  options?: QuizOption[];
  correctOptionIds?: string[];
  acceptedAnswers?: string[];
  explanation?: string;
  tags?: string[];
  difficulty?: string;
  points?: number;
}

export interface QuizOption {
  id: string;
  text: string;
}

export interface QuizSessionQuestion {
  question: QuizQuestion;
  options: QuizOption[];
}
