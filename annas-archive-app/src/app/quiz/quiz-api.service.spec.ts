import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { QuizApiService } from './quiz-api.service';
import { QuizIndex, QuizSubject } from './quiz.models';

describe('QuizApiService', () => {
  let service: QuizApiService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        QuizApiService,
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    service = TestBed.inject(QuizApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('getSubjects', () => {
    it('should fetch quiz subjects index', () => {
      const mockIndex: QuizIndex = {
        version: 1,
        updatedAt: '2026-01-15T00:00:00Z',
        subjects: [
          { id: 'math', title: 'Mathematics', questionCount: 50 },
          { id: 'science', title: 'Science', questionCount: 30 }
        ]
      };

      service.getSubjects().subscribe(result => {
        expect(result.subjects.length).toBe(2);
        expect(result.subjects[0].id).toBe('math');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/quiz/subjects'));
      expect(req.request.method).toBe('GET');
      req.flush(mockIndex);
    });
  });

  describe('getSubject', () => {
    it('should fetch a specific subject by ID', () => {
      const mockSubject: QuizSubject = {
        id: 'math',
        title: 'Mathematics',
        version: 1,
        updatedAt: '2026-01-15T00:00:00Z',
        questionSets: [
          {
            id: 'algebra',
            title: 'Algebra',
            questions: [
              { id: 'q1', type: 'short-answer', prompt: 'What is 2+2?', acceptedAnswers: ['4'] }
            ]
          }
        ]
      };

      service.getSubject('math').subscribe(result => {
        expect(result.id).toBe('math');
        expect(result.questionSets.length).toBe(1);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/quiz/subjects/math'));
      expect(req.request.method).toBe('GET');
      req.flush(mockSubject);
    });
  });

  describe('saveSubject', () => {
    it('should save a subject via PUT request', () => {
      const subject: QuizSubject = {
        id: 'history',
        title: 'History',
        version: 1,
        updatedAt: '2026-01-15T00:00:00Z',
        questionSets: []
      };

      service.saveSubject(subject).subscribe(result => {
        expect(result.id).toBe('history');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/quiz/subjects/history'));
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual(subject);
      req.flush(subject);
    });
  });

  describe('deleteSubject', () => {
    it('should delete a subject by ID', () => {
      service.deleteSubject('old-subject').subscribe(result => {
        expect(result.removed).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/quiz/subjects/old-subject'));
      expect(req.request.method).toBe('DELETE');
      req.flush({ removed: true });
    });
  });
});
