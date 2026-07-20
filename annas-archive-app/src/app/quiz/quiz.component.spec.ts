import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { QuizComponent } from './quiz.component';
import { QuizApiService } from './quiz-api.service';
import { LoggerService } from '../services/logger.service';
import { QuizIndex, QuizSubject } from './quiz.models';

describe('QuizComponent', () => {
  let component: QuizComponent;
  let fixture: ComponentFixture<QuizComponent>;
  let mockQuizApi: jasmine.SpyObj<QuizApiService>;
  let mockLogger: jasmine.SpyObj<LoggerService>;

  const mockIndex: QuizIndex = {
    version: 1,
    updatedAt: '2026-01-15T00:00:00Z',
    subjects: [
      { id: 'math', title: 'Mathematics', questionCount: 10 },
      { id: 'science', title: 'Science', questionCount: 5 }
    ]
  };

  const mockSubject: QuizSubject = {
    id: 'math',
    title: 'Mathematics',
    version: 1,
    updatedAt: '2026-01-15T00:00:00Z',
    questionSets: [
      {
        id: 'basics',
        title: 'Basic Math',
        questions: [
          {
            id: 'q1',
            type: 'multiple-choice',
            prompt: 'What is 2+2?',
            options: [
              { id: 'a', text: '3' },
              { id: 'b', text: '4' },
              { id: 'c', text: '5' }
            ],
            correctOptionIds: ['b']
          },
          {
            id: 'q2',
            type: 'multiple-choice',
            prompt: 'What is 3*3?',
            options: [
              { id: 'a', text: '6' },
              { id: 'b', text: '9' },
              { id: 'c', text: '12' }
            ],
            correctOptionIds: ['b']
          }
        ]
      }
    ]
  };

  beforeEach(async () => {
    mockQuizApi = jasmine.createSpyObj('QuizApiService', ['getSubjects', 'getSubject', 'markQuestionInvalid']);
    mockLogger = jasmine.createSpyObj('LoggerService', ['log', 'warn', 'error']);

    mockQuizApi.getSubjects.and.returnValue(of(mockIndex));
    mockQuizApi.getSubject.and.returnValue(of(mockSubject));

    await TestBed.configureTestingModule({
      imports: [QuizComponent],
      providers: [
        { provide: QuizApiService, useValue: mockQuizApi },
        { provide: LoggerService, useValue: mockLogger }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(QuizComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    component.ngOnDestroy();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should initialize with loading status', () => {
    expect(component.status).toBe('loading');
  });

  describe('ngOnInit', () => {
    it('should load index on init', fakeAsync(() => {
      fixture.detectChanges();
      tick();

      expect(mockQuizApi.getSubjects).toHaveBeenCalled();
    }));

    it('should auto-select first subject', fakeAsync(() => {
      fixture.detectChanges();
      tick();

      expect(component.subjectId).toBe('math');
      expect(mockQuizApi.getSubject).toHaveBeenCalledWith('math');
    }));
  });

  describe('loadIndex', () => {
    it('should set index and load first subject', fakeAsync(() => {
      component.loadIndex();
      tick();

      expect(component.index).toEqual(mockIndex);
    }));

    it('should set error status on failure', fakeAsync(() => {
      mockQuizApi.getSubjects.and.returnValue(throwError(() => new Error('Network error')));

      component.loadIndex();
      tick();

      expect(component.status).toBe('error');
      expect(mockLogger.error).toHaveBeenCalled();
    }));

    it('should set start status if no subjects', fakeAsync(() => {
      mockQuizApi.getSubjects.and.returnValue(of({
        version: 1,
        updatedAt: '2026-01-15',
        subjects: []
      }));

      component.loadIndex();
      tick();

      expect(component.status).toBe('start');
    }));
  });

  describe('selectSubject', () => {
    it('should load subject and build question bank', fakeAsync(() => {
      component.selectSubject('math');
      tick();

      expect(component.subject).toEqual(mockSubject);
      expect(component.questionBank.length).toBe(2);
      expect(component.status).toBe('start');
    }));

    it('should do nothing for empty subjectId', () => {
      component.selectSubject('');

      expect(mockQuizApi.getSubject).not.toHaveBeenCalled();
    });

    it('should set error status on failure', fakeAsync(() => {
      mockQuizApi.getSubject.and.returnValue(throwError(() => new Error('Not found')));

      component.selectSubject('invalid');
      tick();

      expect(component.status).toBe('error');
    }));
  });

  describe('subjectDisplay', () => {
    it('should return subject title when available', fakeAsync(() => {
      component.selectSubject('math');
      tick();

      expect(component.subjectDisplay).toBe('Mathematics');
    }));

    it('should return Quiz when no subject', () => {
      component.subject = null;

      expect(component.subjectDisplay).toBe('Quiz');
    });
  });

  describe('handleStart', () => {
    beforeEach(fakeAsync(() => {
      fixture.detectChanges();
      tick();
    }));

    it('should start quiz with questions', () => {
      component.numQuestions = 2;

      component.handleStart();

      expect(component.status).toBe('quiz');
      expect(component.quizQuestions.length).toBeLessThanOrEqual(2);
      expect(component.currentIndex).toBe(0);
    });

    it('should initialize user answers array', () => {
      component.numQuestions = 2;

      component.handleStart();

      expect(component.userAnswers.length).toBe(component.quizQuestions.length);
      expect(component.userAnswers.every(a => a === null)).toBe(true);
    });

    it('should alert if no questions available', () => {
      spyOn(window, 'alert');
      // Mark all questions as mastered
      component.progress['math'] = {
        mastered: [1, 2],
        flagged: []
      };

      component.handleStart();

      expect(window.alert).toHaveBeenCalled();
      expect(component.status).toBe('start');
    });
  });

  describe('handleSelect', () => {
    beforeEach(fakeAsync(() => {
      fixture.detectChanges();
      tick();
      component.numQuestions = 2;
      component.handleStart();
    }));

    it('should record selected answer', () => {
      const option = { id: 'b', label: 'B', text: '4' };

      component.handleSelect(option);

      expect(component.userAnswers[0]).toBe('b');
    });

    it('should not allow selection after answer is marked', () => {
      component.questionAnswered[0] = true;
      const option = { id: 'c', label: 'C', text: '5' };

      component.handleSelect(option);

      expect(component.userAnswers[0]).toBeNull();
    });
  });

  describe('handleToggleMulti', () => {
    beforeEach(fakeAsync(() => {
      fixture.detectChanges();
      tick();
      component.numQuestions = 1;
      component.handleStart();
    }));

    it('should add option to multi-select', () => {
      const option = { id: 'a', label: 'A', text: 'Option A' };

      component.handleToggleMulti(option);

      expect(component.userAnswers[0]).toEqual(['a']);
    });

    it('should remove option if already selected', () => {
      component.userAnswers[0] = ['a', 'b'];
      const option = { id: 'a', label: 'A', text: 'Option A' };

      component.handleToggleMulti(option);

      expect(component.userAnswers[0]).toEqual(['b']);
    });
  });

  describe('progress tracking', () => {
    beforeEach(fakeAsync(() => {
      fixture.detectChanges();
      tick();
    }));

    it('should return empty mastered set initially', () => {
      expect(component.masteredSet.size).toBe(0);
    });

    it('should return empty flagged set initially', () => {
      expect(component.flaggedSet.size).toBe(0);
    });

    it('should filter available questions based on progress', () => {
      component.progress['math'] = {
        mastered: [1],
        flagged: []
      };

      expect(component.availableQuestions.length).toBe(1);
    });
  });

  describe('maxAvailableQuestions', () => {
    beforeEach(fakeAsync(() => {
      fixture.detectChanges();
      tick();
    }));

    it('should return at least 1', () => {
      component.progress['math'] = {
        mastered: [1, 2],
        flagged: []
      };

      expect(component.maxAvailableQuestions).toBeGreaterThanOrEqual(1);
    });
  });

  describe('endQuizEarly', () => {
    beforeEach(fakeAsync(() => {
      fixture.detectChanges();
      tick();
      component.numQuestions = 2;
      component.handleStart();
    }));

    it('should set status to results when user confirms', () => {
      spyOn(window, 'confirm').and.returnValue(true);

      component.endQuizEarly();

      expect(component.status).toBe('results');
    });

    it('should not change status when user cancels', () => {
      spyOn(window, 'confirm').and.returnValue(false);

      component.endQuizEarly();

      expect(component.status).toBe('quiz');
    });
  });

  describe('markQuestionInvalid', () => {
    beforeEach(fakeAsync(() => {
      fixture.detectChanges();
      tick();
      component.numQuestions = 2;
      component.handleStart();
      mockQuizApi.markQuestionInvalid.and.returnValue(of({ success: true }));
    }));

    it('should do nothing when user cancels prompt', () => {
      spyOn(window, 'prompt').and.returnValue(null);

      const question = component.quizQuestions[0];
      component.markQuestionInvalid(question);

      expect(mockQuizApi.markQuestionInvalid).not.toHaveBeenCalled();
    });

    it('should call API when user enters reason', fakeAsync(() => {
      spyOn(window, 'prompt').and.returnValue('Bad question');
      spyOn(window, 'alert');

      const question = component.quizQuestions[0];
      component.markQuestionInvalid(question);
      tick();

      expect(mockQuizApi.markQuestionInvalid).toHaveBeenCalledWith('math', question.id, 'Bad question');
    }));

    it('should call API with undefined when user submits empty reason', fakeAsync(() => {
      spyOn(window, 'prompt').and.returnValue('');
      spyOn(window, 'alert');

      const question = component.quizQuestions[0];
      component.markQuestionInvalid(question);
      tick();

      expect(mockQuizApi.markQuestionInvalid).toHaveBeenCalledWith('math', question.id, undefined);
    }));

    it('should remove question from question bank on success', fakeAsync(() => {
      spyOn(window, 'prompt').and.returnValue('Bad question');
      spyOn(window, 'alert');

      const question = component.quizQuestions[0];
      const initialBankSize = component.questionBank.length;

      component.markQuestionInvalid(question);
      tick();

      expect(component.questionBank.length).toBe(initialBankSize - 1);
      expect(component.questionBank.some(q => q.id === question.id)).toBe(false);
    }));

    it('should flag question locally on success', fakeAsync(() => {
      spyOn(window, 'prompt').and.returnValue('Bad question');
      spyOn(window, 'alert');

      const question = component.quizQuestions[0];
      component.markQuestionInvalid(question);
      tick();

      expect(component.flaggedSet.has(question.number)).toBe(true);
    }));

    it('should alert on success', fakeAsync(() => {
      spyOn(window, 'prompt').and.returnValue('Bad question');
      spyOn(window, 'alert');

      const question = component.quizQuestions[0];
      component.markQuestionInvalid(question);
      tick();

      expect(window.alert).toHaveBeenCalledWith('Question marked as invalid and removed from the quiz pool.');
    }));

    it('should alert on error', fakeAsync(() => {
      mockQuizApi.markQuestionInvalid.and.returnValue(throwError(() => new Error('Server error')));
      spyOn(window, 'prompt').and.returnValue('Bad question');
      spyOn(window, 'alert');

      const question = component.quizQuestions[0];
      component.markQuestionInvalid(question);
      tick();

      expect(window.alert).toHaveBeenCalledWith('Failed to mark question as invalid. Please try again.');
      expect(mockLogger.error).toHaveBeenCalled();
    }));
  });
});
