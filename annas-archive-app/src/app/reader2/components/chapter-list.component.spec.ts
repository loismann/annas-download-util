import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChapterListComponent } from './chapter-list.component';
import { ChapterInfo } from '../reader2.models';

function chapter(
  id: number, title: string, hasSummary = false, summaryIsStale = false
): ChapterInfo {
  return { id, title, level: 0, wordCount: 1200, hasSummary, summaryIsStale };
}

describe('ChapterListComponent', () => {
  let fixture: ComponentFixture<ChapterListComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ChapterListComponent] }).compileComponents();
    fixture = TestBed.createComponent(ChapterListComponent);
  });

  function render(chapters: ChapterInfo[], currentIndex = 0): HTMLElement {
    fixture.componentRef.setInput('chapters', chapters);
    fixture.componentRef.setInput('currentIndex', currentIndex);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('lists the chapters in the order they were given', () => {
    const titles = Array.from(
      render([chapter(0, 'Preface'), chapter(1, 'Chapter One')])
        .querySelectorAll('.title'),
      node => node.textContent?.trim());

    expect(titles).toEqual(['Preface', 'Chapter One']);
  });

  /**
   * The whole reason `hasSummary` is served. A reader who cannot see what is
   * already paid for buys it a second time, and the server cannot be asked
   * "have I bought this" any more cheaply than it already answers here.
   */
  it('marks the chapters that are already summarised', () => {
    const buttons = render([chapter(0, 'Preface', true), chapter(1, 'Chapter One')])
      .querySelectorAll('.chapter');

    expect(buttons[0].querySelector('.summarised')).not.toBeNull();
    expect(buttons[1].querySelector('.summarised')).toBeNull();
  });

  it('says what the mark means, rather than showing a bare tick', () => {
    const mark = render([chapter(0, 'Preface', true)]).querySelector('.summarised');

    expect(mark?.getAttribute('aria-label')).toBe('Already summarised');
  });

  it('marks which chapter is being read', () => {
    const buttons = render([chapter(0, 'Preface'), chapter(1, 'Chapter One')], 1)
      .querySelectorAll('.chapter');

    expect(buttons[1].getAttribute('aria-current')).toBe('true');
    expect(buttons[0].getAttribute('aria-current')).toBeNull();
  });

  it('reports which chapter was chosen', () => {
    let chosen = -1;
    fixture.componentInstance.select.subscribe((i: number) => { chosen = i; });

    render([chapter(0, 'Preface'), chapter(1, 'Chapter One')])
      .querySelectorAll<HTMLButtonElement>('.chapter')[1].click();

    expect(chosen).toBe(1);
  });

  it('says so when there is nothing to list', () => {
    expect(render([]).querySelector('.empty')?.textContent).toContain('Nothing indexed');
  });
});
