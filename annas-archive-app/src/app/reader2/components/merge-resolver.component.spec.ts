import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MergeAnswer, MergeResolverComponent } from './merge-resolver.component';
import { CandidateMerge } from '../reader2.models';
import { actor, question } from '../testing/cast';

describe('MergeResolverComponent', () => {
  let fixture: ComponentFixture<MergeResolverComponent>;
  let answers: MergeAnswer[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [MergeResolverComponent] }).compileComponents();
    fixture = TestBed.createComponent(MergeResolverComponent);
    answers = [];
    fixture.componentInstance.resolve.subscribe((a: MergeAnswer) => answers.push(a));
  });

  function render(questions: CandidateMerge[]): HTMLElement {
    fixture.componentRef.setInput('questions', questions);
    fixture.componentRef.setInput('actors', [actor('a1', 'Pierre'), actor('a2', 'Pyotr Bezukhov')]);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function press(label: string): void {
    Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'))
      .find(b => b.textContent?.trim() === label)!.click();
  }

  it('asks an alias question in words, with the actor named', () => {
    const page = render([question('m1', 'a1', 'The Bear')]);

    expect(page.querySelector('.ask')?.textContent).toContain('The Bear');
    expect(page.querySelector('.ask')?.textContent).toContain('Pierre');
  });

  it('asks a two-actor question with both named', () => {
    const page = render([question('m1', 'a1', 'Pyotr', 'a2')]);

    expect(page.querySelector('.ask')?.textContent).toContain('Pierre');
    expect(page.querySelector('.ask')?.textContent).toContain('Pyotr Bezukhov');
  });

  it('shows the merger’s reason, so the reader is not answering blind', () => {
    expect(render([question('m1', 'a1', 'The Bear')]).querySelector('.reason')?.textContent)
      .toContain('not certain');
  });

  it('answers yes with the question’s id', () => {
    render([question('m1', 'a1', 'The Bear')]);
    press('Yes, same person');

    expect(answers).toEqual([{ mergeId: 'm1', accept: true }]);
  });

  it('answers no with the question’s id', () => {
    render([question('m1', 'a1', 'Pyotr', 'a2')]);
    press('Keep apart');

    expect(answers).toEqual([{ mergeId: 'm1', accept: false }]);
  });

  /** An unanswerable question the reader can dismiss beats one that vanishes. */
  it('still asks when an actor id resolves to nobody', () => {
    const page = render([question('m1', 'a9', 'The Bear')]);

    expect(page.querySelector('.ask')?.textContent).toContain('a9');
  });
});
