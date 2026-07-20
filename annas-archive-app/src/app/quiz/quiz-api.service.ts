import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { QuizIndex, QuizSubject, InvalidQuestionsFile } from './quiz.models';

@Injectable({ providedIn: 'root' })
export class QuizApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5001'
    : '';
  private readonly baseUrl = `${this.apiHost}/api/quiz`;

  constructor(private http: HttpClient) {}

  getSubjects(): Observable<QuizIndex> {
    return this.http.get<QuizIndex>(`${this.baseUrl}/subjects`);
  }

  getSubject(subjectId: string): Observable<QuizSubject> {
    return this.http.get<QuizSubject>(`${this.baseUrl}/subjects/${subjectId}`);
  }

  saveSubject(subject: QuizSubject): Observable<QuizSubject> {
    return this.http.put<QuizSubject>(`${this.baseUrl}/subjects/${subject.id}`, subject);
  }

  deleteSubject(subjectId: string): Observable<{ removed: boolean }> {
    return this.http.delete<{ removed: boolean }>(`${this.baseUrl}/subjects/${subjectId}`);
  }

  getInvalidQuestions(): Observable<InvalidQuestionsFile> {
    return this.http.get<InvalidQuestionsFile>(`${this.baseUrl}/invalid-questions`);
  }

  markQuestionInvalid(subjectId: string, questionId: string, reason?: string): Observable<{ success: boolean }> {
    return this.http.post<{ success: boolean }>(
      `${this.baseUrl}/subjects/${subjectId}/questions/${questionId}/mark-invalid`,
      { reason }
    );
  }
}
