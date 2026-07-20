import { buildSessionQuestions, scoreAnswer } from './quiz-utils';
import { QuizMode, QuizSubject } from './quiz.models';

describe('quiz-utils', () => {
  const subject: QuizSubject = {
    id: 'science',
    title: 'Science',
    version: 1,
    updatedAt: '2024-01-01T00:00:00Z',
    questionSets: [
      {
        id: 'core',
        title: 'Core',
        questions: [
          {
            id: 'q1',
            type: 'multiple-choice',
            prompt: 'Pick A',
            options: [
              { id: 'a', text: 'A' },
              { id: 'b', text: 'B' }
            ],
            correctOptionIds: ['a']
          },
          {
            id: 'q2',
            type: 'multi-select',
            prompt: 'Pick A and B',
            options: [
              { id: 'a', text: 'A' },
              { id: 'b', text: 'B' },
              { id: 'c', text: 'C' }
            ],
            correctOptionIds: ['a', 'b']
          },
          {
            id: 'q3',
            type: 'true-false',
            prompt: 'True statement?',
            options: [
              { id: 'true', text: 'True' },
              { id: 'false', text: 'False' }
            ],
            correctOptionIds: ['true']
          }
        ]
      }
    ]
  };

  it('buildSessionQuestions respects questionCount', () => {
    const mode: QuizMode = {
      id: 'practice',
      label: 'Practice',
      questionCount: 2,
      shuffleQuestions: false,
      shuffleOptions: false
    };

    const session = buildSessionQuestions(subject, mode);

    expect(session.length).toBe(2);
    expect(session[0].question.id).toBe('q1');
  });

  it('scoreAnswer matches multi-select answers', () => {
    const question = subject.questionSets[0].questions[1];
    expect(scoreAnswer(question, ['a', 'b'])).toBeTrue();
    expect(scoreAnswer(question, ['a'])).toBeFalse();
  });

  it('scoreAnswer matches true-false answers', () => {
    const question = subject.questionSets[0].questions[2];
    expect(scoreAnswer(question, 'true')).toBeTrue();
    expect(scoreAnswer(question, 'false')).toBeFalse();
  });
});
