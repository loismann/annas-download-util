import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ThreadPanelComponent } from './thread-panel.component';
import { StoryThread } from '../reader2.models';
import { actor, contents, thread } from '../testing/cast';

describe('ThreadPanelComponent', () => {
  let fixture: ComponentFixture<ThreadPanelComponent>;

  /** Two front-matter entries first, so a chapter's index and its number disagree. */
  const CONTENTS = contents(
    'Cover', 'Copyright', 'Chapter One', 'Chapter Two', 'Chapter Three', 'Chapter Four',
    'Chapter Five', 'Chapter Six', 'Chapter Seven', 'Chapter Eight', 'Chapter Nine',
    'Chapter Ten', 'Chapter Eleven', 'Chapter Twelve', 'Chapter Thirteen', 'Chapter Fourteen',
    'Chapter Fifteen', 'Chapter Sixteen', 'Chapter Seventeen', 'Chapter Eighteen');

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ThreadPanelComponent] }).compileComponents();
    fixture = TestBed.createComponent(ThreadPanelComponent);
  });

  function render(threads: StoryThread[], currentChapter = 20): HTMLElement {
    fixture.componentRef.setInput('threads', threads);
    fixture.componentRef.setInput('actors', [actor('a1', 'Dolokhov')]);
    fixture.componentRef.setInput('vocabulary',
      { actors: 'Characters', groups: 'Factions', threads: 'Plot threads' });
    fixture.componentRef.setInput('currentChapter', currentChapter);
    fixture.componentRef.setInput('chapters', CONTENTS);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('sets a dormant thread apart and says how long it has been quiet', () => {
    const page = render([thread('t1', "Dolokhov's debt", 'Dormant', { lastAdvancedChapter: 5 })]);

    expect(page.querySelector('.thread.dormant')).not.toBeNull();
    expect(page.querySelector('.quiet')?.textContent).toContain('Chapter Four');
    expect(page.querySelector('.quiet')?.textContent).toContain('15 chapters ago');
  });

  it('flags a return with the length of the gap', () => {
    const page = render([thread('t1', 'The debt', 'Active', {
      returnedInChapter: 18, returnedAfterChapters: 12
    })]);

    expect(page.querySelector('.returned')?.textContent).toContain('Chapter Seventeen');
    expect(page.querySelector('.returned')?.textContent).toContain('after 12 chapters');
  });

  it('puts the quiet ones first and the finished ones last', () => {
    const names = Array.from(render([
      thread('t1', 'Finished', 'Resolved'),
      thread('t2', 'Running', 'Active'),
      thread('t3', 'Quiet', 'Dormant')
    ]).querySelectorAll('.name'), n => n.textContent?.trim());

    expect(names).toEqual(['Quiet', 'Running', 'Finished']);
  });

  it('names who is in a thread', () => {
    const page = render([thread('t1', 'The debt', 'Active', { participantIds: ['a1'] })]);

    expect(page.querySelector('.who')?.textContent).toContain('Dolokhov');
  });

  it('shows the latest movements, newest first', () => {
    const page = render([thread('t1', 'The debt', 'Active', {
      beats: [
        { chapter: 2, whatMoved: 'the game' },
        { chapter: 9, whatMoved: 'the demand' }
      ]
    })]);

    const beats = Array.from(page.querySelectorAll('.beats li'), b => b.textContent);
    expect(beats[0]).toContain('the demand');
  });

  it('says so when there are no threads yet', () => {
    expect(render([]).querySelector('.empty')?.textContent).toContain('plot threads');
  });
});
