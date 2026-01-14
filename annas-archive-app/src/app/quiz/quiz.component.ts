import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { QuizApiService } from './quiz-api.service';
import { QuizIndex, QuizQuestion, QuizSubject } from './quiz.models';
import { scoreAnswer } from './quiz-utils';
import { LoggerService } from '../services/logger.service';

interface QuizOptionDisplay {
  id: string;
  label: string;
  text: string;
}

interface QuizCatalogQuestion {
  id: string;
  number: number;
  text: string;
  type: QuizQuestion['type'];
  multiSelect: boolean;
  options: QuizOptionDisplay[];
  correctOptionIds: string[];
}

type QuizStatus = 'loading' | 'start' | 'quiz' | 'results' | 'error';

type CelebrationLevel = 'quiz' | 'milestone' | 'all' | null;

interface CelebrationState {
  level: CelebrationLevel;
  message: string;
}

interface ConfettiPiece {
  id: string;
  left: number;
  duration: number;
  delay: number;
  rotate: number;
  color: string;
}

@Component({
  selector: 'app-quiz',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule],
  templateUrl: './quiz.component.html',
  styleUrls: ['./quiz.component.css']
})
export class QuizComponent implements OnInit, OnDestroy {
  index: QuizIndex | null = null;
  subject: QuizSubject | null = null;
  subjectId = '';
  status: QuizStatus = 'loading';

  progress: Record<string, { mastered: number[]; flagged: number[] }> = {};
  questionBank: QuizCatalogQuestion[] = [];
  quizQuestions: QuizCatalogQuestion[] = [];
  userAnswers: Array<string | string[] | null> = [];
  questionAnswered: boolean[] = [];
  currentIndex = 0;
  numQuestions = 10;

  celebration: CelebrationState = { level: null, message: '' };
  confettiPieces: ConfettiPiece[] = [];
  private celebrationTimer: number | null = null;
  private prevMasteredCount = 0;
  prefersReducedMotion = false;
  private motionQuery?: MediaQueryList;
  private motionHandler?: () => void;

  private audioCtx?: AudioContext;

  constructor(
    private quizApi: QuizApiService,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
    this.loadIndex();
    this.listenForReducedMotion();
    window.addEventListener('keydown', this.handleKeyDown);
  }

  ngOnDestroy(): void {
    if (this.celebrationTimer) {
      window.clearTimeout(this.celebrationTimer);
    }
    window.removeEventListener('keydown', this.handleKeyDown);
    if (this.motionQuery && this.motionHandler) {
      this.motionQuery.removeEventListener('change', this.motionHandler);
    }
  }

  loadIndex(): void {
    this.status = 'loading';
    this.quizApi.getSubjects().subscribe({
      next: index => {
        this.index = index;
        if (index.subjects.length > 0) {
          this.selectSubject(index.subjects[0].id);
        } else {
          this.status = 'start';
        }
      },
      error: err => {
        this.logger.error(err);
        this.status = 'error';
      }
    });
  }

  selectSubject(subjectId: string): void {
    if (!subjectId) return;
    this.subjectId = subjectId;
    this.status = 'loading';
    this.quizApi.getSubject(subjectId).subscribe({
      next: subject => {
        this.subject = subject;
        this.questionBank = this.buildQuestionBank(subject);
        this.prevMasteredCount = this.masteredSet.size;
        this.status = 'start';
        this.resetQuizSession();
      },
      error: err => {
        this.logger.error(err);
        this.status = 'error';
      }
    });
  }

  get subjectDisplay(): string {
    return this.subject?.title || 'Quiz';
  }

  get masteredSet(): Set<number> {
    return new Set(this.progress[this.subjectId]?.mastered || []);
  }

  get flaggedSet(): Set<number> {
    return new Set(this.progress[this.subjectId]?.flagged || []);
  }

  get flaggedList(): number[] {
    return Array.from(this.flaggedSet).sort((a, b) => a - b);
  }

  get maxAvailableQuestions(): number {
    return Math.max(this.availableQuestions.length, 1);
  }

  get availableQuestions(): QuizCatalogQuestion[] {
    const mastered = this.masteredSet;
    const flagged = this.flaggedSet;
    return this.questionBank.filter(q => !mastered.has(q.number) && !flagged.has(q.number));
  }

  handleStart(): void {
    if (this.availableQuestions.length === 0) {
      alert('No available questions. Reset progress to include mastered/flagged questions again.');
      return;
    }
    const count = Math.min(this.numQuestions, this.availableQuestions.length);
    const shuffled = [...this.availableQuestions].sort(() => Math.random() - 0.5).slice(0, count);
    this.quizQuestions = shuffled;
    this.userAnswers = new Array(count).fill(null);
    this.questionAnswered = new Array(count).fill(false);
    this.currentIndex = 0;
    this.status = 'quiz';
  }

  handleSelect(option: QuizOptionDisplay): void {
    if (this.questionAnswered[this.currentIndex]) return;
    const next = [...this.userAnswers];
    next[this.currentIndex] = option.id;
    this.userAnswers = next;
    this.markAnswered();
  }

  handleToggleMulti(option: QuizOptionDisplay): void {
    if (this.questionAnswered[this.currentIndex]) return;
    const current = this.userAnswers[this.currentIndex];
    const arr = Array.isArray(current) ? [...current] : [];
    const idx = arr.indexOf(option.id);
    if (idx >= 0) arr.splice(idx, 1);
    else arr.push(option.id);
    const next = [...this.userAnswers];
    next[this.currentIndex] = arr;
    this.userAnswers = next;
  }

  submitMulti(): void {
    const ans = this.userAnswers[this.currentIndex];
    if (!Array.isArray(ans) || ans.length === 0) {
      alert('Select at least one answer');
      return;
    }
    this.markAnswered();
  }

  handleFlag(questionNumber: number): void {
    this.updateProgress(({ flagged, mastered }) => {
      flagged.add(questionNumber);
      mastered.delete(questionNumber);
    });
  }

  nextQuestion(): void {
    if (this.currentIndex < this.quizQuestions.length - 1) {
      this.currentIndex += 1;
    } else {
      this.status = 'results';
      this.celebrate('quiz', 'Quiz complete! Nice work.');
    }
  }

  prevQuestion(): void {
    if (this.currentIndex > 0) {
      this.currentIndex -= 1;
    }
  }

  resetProgressCurrent(): void {
    this.progress = {
      ...this.progress,
      [this.subjectId]: { mastered: [], flagged: [] }
    };
  }

  resetAll(): void {
    this.progress = {};
  }

  resetQuizSession(): void {
    this.quizQuestions = [];
    this.userAnswers = [];
    this.questionAnswered = [];
    this.currentIndex = 0;
  }

  get currentQuestion(): QuizCatalogQuestion | null {
    return this.quizQuestions[this.currentIndex] ?? null;
  }

  get questionCount(): number {
    return this.quizQuestions.length;
  }

  get progressLabel(): string {
    return `${this.currentIndex + 1}`;
  }

  get progressTotal(): string {
    return `${this.questionCount}`;
  }

  getCorrectAnswerText(question: QuizCatalogQuestion): string {
    if (question.type === 'true-false') {
      return question.correctOptionIds.includes('true') ? 'True' : 'False';
    }
    return question.correctOptionIds
      .map(id => question.options.find(opt => opt.id === id)?.label || id)
      .join(', ');
  }

  isAnswerCorrect(question: QuizCatalogQuestion, answer: string | string[] | null): boolean {
    return this.checkAnswer(question, answer);
  }

  handleGrading(question: QuizCatalogQuestion, index: number): { correctAnswer: string; isCorrect: boolean } {
    const answer = this.userAnswers[index];
    const isCorrect = this.checkAnswer(question, answer);
    return { correctAnswer: this.getCorrectAnswerText(question), isCorrect };
  }

  isOptionCorrect(option: QuizOptionDisplay, question: QuizCatalogQuestion): boolean {
    return question.correctOptionIds.includes(option.id);
  }

  isOptionSelected(option: QuizOptionDisplay): boolean {
    const user = this.userAnswers[this.currentIndex];
    if (Array.isArray(user)) return user.includes(option.id);
    return user === option.id;
  }

  getOptionClasses(option: QuizOptionDisplay): string {
    const question = this.currentQuestion;
    if (!question) return 'mc-option';

    const classes = ['mc-option'];
    const user = this.userAnswers[this.currentIndex];
    const isMulti = question.multiSelect;
    const selected = isMulti ? Array.isArray(user) && user.includes(option.id) : user === option.id;
    const grading = this.handleGrading(question, this.currentIndex);

    if (this.questionAnswered[this.currentIndex]) {
      if (isMulti) {
        if (selected) classes.push(this.isOptionCorrect(option, question) ? 'correct' : 'incorrect');
        if (this.isOptionCorrect(option, question)) classes.push('show-correct');
      } else {
        if (selected) classes.push(grading.isCorrect ? 'correct' : 'incorrect');
        if (this.isOptionCorrect(option, question)) classes.push('show-correct');
      }
    } else if (selected) {
      classes.push('selected');
    }

    return classes.join(' ');
  }

  getResultsSummary(): { correct: number; total: number; percent: number } {
    let correctCount = 0;
    this.quizQuestions.forEach((question, idx) => {
      if (this.checkAnswer(question, this.userAnswers[idx])) correctCount += 1;
    });
    const percent = this.quizQuestions.length
      ? Math.round((correctCount / this.quizQuestions.length) * 100)
      : 0;
    return { correct: correctCount, total: this.quizQuestions.length, percent };
  }

  formatUserAnswer(question: QuizCatalogQuestion, answer: string | string[] | null): string {
    if (!answer) return '(no answer)';
    if (Array.isArray(answer)) {
      if (answer.length === 0) return '(no answer)';
      return answer
        .map(id => question.options.find(opt => opt.id === id)?.label || id)
        .join(', ');
    }
    return question.options.find(opt => opt.id === answer)?.label || answer;
  }

  updateNumQuestions(value: string): void {
    const parsed = parseInt(value, 10);
    const max = Math.max(this.availableQuestions.length, 1);
    const clamped = Math.max(1, Math.min(Number.isNaN(parsed) ? 1 : parsed, max));
    this.numQuestions = clamped;
  }

  celebrate(level: CelebrationLevel, message: string): void {
    if (!level) return;
    if (this.celebrationTimer) window.clearTimeout(this.celebrationTimer);
    this.celebration = { level, message };
    this.confettiPieces = this.prefersReducedMotion ? [] : this.buildConfetti(level);
    this.playCelebrateSound(level);
    this.celebrationTimer = window.setTimeout(() => {
      this.celebration = { level: null, message: '' };
      this.confettiPieces = [];
    }, 3800);
  }

  private markAnswered(): void {
    const answered = [...this.questionAnswered];
    answered[this.currentIndex] = true;
    this.questionAnswered = answered;

    const question = this.currentQuestion;
    if (!question) return;
    const isCorrect = this.checkAnswer(question, this.userAnswers[this.currentIndex]);
    if (isCorrect) this.playCorrectSound();
    else this.playIncorrectSound();

    this.updateProgress(({ mastered }) => {
      if (isCorrect) mastered.add(question.number);
      else mastered.delete(question.number);
    });

    this.handleMasteryCelebration();
  }

  private handleMasteryCelebration(): void {
    const total = this.questionBank.length;
    const masteredCount = this.masteredSet.size;
    if (total > 0) {
      if (masteredCount === total && this.prevMasteredCount !== masteredCount) {
        this.celebrate('all', 'All questions mastered! Amazing!');
      } else if (Math.floor(masteredCount / 10) > Math.floor(this.prevMasteredCount / 10) && masteredCount > 0) {
        this.celebrate('milestone', `Mastered ${masteredCount} questions!`);
      }
    }
    this.prevMasteredCount = masteredCount;
  }

  private updateProgress(updater: (sets: { mastered: Set<number>; flagged: Set<number> }) => void): void {
    const current = this.progress[this.subjectId] || { mastered: [], flagged: [] };
    const mastered = new Set(current.mastered);
    const flagged = new Set(current.flagged);
    updater({ mastered, flagged });
    this.progress = {
      ...this.progress,
      [this.subjectId]: { mastered: Array.from(mastered), flagged: Array.from(flagged) }
    };
  }

  checkAnswer(question: QuizCatalogQuestion, answer: string | string[] | null): boolean {
    if (!answer) return false;
    if (question.multiSelect) {
      const correct = new Set(question.correctOptionIds);
      const selected = new Set(Array.isArray(answer) ? answer : [answer]);
      if (correct.size !== selected.size) return false;
      for (const id of correct) {
        if (!selected.has(id)) return false;
      }
      return true;
    }
    return scoreAnswer({
      id: question.id,
      type: question.type,
      prompt: question.text,
      options: question.options.map(opt => ({ id: opt.id, text: opt.text })),
      correctOptionIds: question.correctOptionIds
    }, Array.isArray(answer) ? answer[0] : answer);
  }

  private buildQuestionBank(subject: QuizSubject): QuizCatalogQuestion[] {
    const bank: QuizCatalogQuestion[] = [];
    let counter = 1;
    subject.questionSets.forEach(set => {
      set.questions.forEach(question => {
        bank.push(this.toCatalogQuestion(question, counter));
        counter += 1;
      });
    });
    return bank;
  }

  private toCatalogQuestion(question: QuizQuestion, fallbackNumber: number): QuizCatalogQuestion {
    const number = this.extractQuestionNumber(question.id) ?? fallbackNumber;
    const options = (question.options && question.options.length > 0)
      ? question.options.map(opt => ({
          id: opt.id,
          label: `${opt.id}) ${opt.text}`,
          text: opt.text
        }))
      : [
          { id: 'true', label: 'True', text: 'True' },
          { id: 'false', label: 'False', text: 'False' }
        ];

    return {
      id: question.id,
      number,
      text: question.prompt,
      type: question.type,
      multiSelect: question.type === 'multi-select',
      options,
      correctOptionIds: question.correctOptionIds ?? []
    };
  }

  private extractQuestionNumber(id: string): number | null {
    const match = id.match(/(\d+)$/);
    if (!match) return null;
    const parsed = parseInt(match[1], 10);
    return Number.isNaN(parsed) ? null : parsed;
  }

  private listenForReducedMotion(): void {
    this.motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
    this.motionHandler = () => {
      this.prefersReducedMotion = !!this.motionQuery?.matches;
    };
    this.motionHandler();
    this.motionQuery.addEventListener('change', this.motionHandler);
  }

  private handleKeyDown = (event: KeyboardEvent): void => {
    if (event.key === 'Enter' && this.status === 'quiz') {
      this.nextQuestion();
    }
  };

  private playTone(frequency: number, duration = 0.2): void {
    if (!(window.AudioContext || (window as any).webkitAudioContext)) return;
    if (!this.audioCtx) {
      const Ctx = window.AudioContext || (window as any).webkitAudioContext;
      this.audioCtx = new Ctx();
    }
    const osc = this.audioCtx.createOscillator();
    const gain = this.audioCtx.createGain();
    osc.type = 'sine';
    osc.frequency.value = frequency;
    gain.gain.setValueAtTime(0.15, this.audioCtx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.0001, this.audioCtx.currentTime + duration);
    osc.connect(gain).connect(this.audioCtx.destination);
    osc.start();
    osc.stop(this.audioCtx.currentTime + duration);
  }

  private playPattern(pattern: number[]): void {
    pattern.forEach((freq, index) => {
      window.setTimeout(() => this.playTone(freq), index * 80);
    });
  }

  private playCorrectSound(): void {
    const patterns = [[880], [1046, 1318], [988, 1175], [880, 1046, 1318]];
    const pattern = patterns[Math.floor(Math.random() * patterns.length)];
    this.playPattern(pattern);
  }

  private playIncorrectSound(): void {
    const patterns = [[220], [247, 196], [262, 220], [196, 175]];
    const pattern = patterns[Math.floor(Math.random() * patterns.length)];
    this.playPattern(pattern);
  }

  private playCelebrateSound(level: CelebrationLevel): void {
    if (!level) return;
    const patterns: Record<string, number[]> = {
      quiz: [523, 659, 784],
      milestone: [392, 523, 659, 784],
      all: [329, 392, 523, 659, 784, 880]
    };
    this.playPattern(patterns[level]);
  }

  private buildConfetti(level: CelebrationLevel): ConfettiPiece[] {
    const now = Date.now();
    const count = level === 'all' ? 140 : level === 'milestone' ? 90 : 70;
    const colors = ['#ff6b6b', '#ffd166', '#6bcBef', '#9b6bff', '#4ade80', '#f497da'];
    return Array.from({ length: count }).map((_, idx) => ({
      id: `${now}-${idx}`,
      left: Math.random() * 100,
      duration: 2200 + Math.random() * 1500,
      delay: Math.random() * 250,
      rotate: Math.random() * 360,
      color: colors[idx % colors.length]
    }));
  }
}
