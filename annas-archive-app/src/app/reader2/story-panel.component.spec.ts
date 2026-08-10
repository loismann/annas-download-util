import { ComponentFixture, TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { of } from 'rxjs';
import { StoryPanelComponent } from './story-panel.component';
import { StoryStore } from './services/story-store';
import { ReaderTasks } from './services/reader-tasks';
import { ReaderConfirm } from './services/reader-confirm';
import { ReaderStore } from './services/reader-store';
import { Reader2ApiService } from './services/reader2-api.service';
import { Book, ChapterInfo, Lens, StoryModel } from './reader2.models';
import { actor, model, question } from './testing/cast';

describe('StoryPanelComponent', () => {
  let fixture: ComponentFixture<StoryPanelComponent>;
  let api: jasmine.SpyObj<Reader2ApiService>;
  let reader: {
    book: WritableSignal<Book | null>;
    lenses: WritableSignal<Lens[]>;
    chapters: WritableSignal<ChapterInfo[]>;
    chapterIndex: WritableSignal<number>;
  };

  const LOADED: StoryModel = model({
    actors: [actor('a1', 'Pierre', 'Major'), actor('a2', 'Pyotr Bezukhov', 'Minor')],
    openQuestions: [question('m1', 'a1', 'Pyotr Bezukhov', 'a2')]
  });

  beforeEach(async () => {
    api = jasmine.createSpyObj<Reader2ApiService>('Reader2ApiService', [
      'storyModel', 'backFillStoryModel', 'resolveMerge'
    ]);
    api.storyModel.and.returnValue(of(LOADED));

    reader = {
      book: signal<Book | null>({
        bookId: 'book-1', fileName: 'novel.epub', title: 'A Novel', authors: [],
        lensKey: 'fiction', addedAtUtc: '', lastOpenedAtUtc: null, isAvailable: true
      }),
      lenses: signal<Lens[]>([]),
      chapters: signal<ChapterInfo[]>([]),
      chapterIndex: signal(4)
    };

    await TestBed.configureTestingModule({
      imports: [StoryPanelComponent],
      providers: [
        StoryStore, ReaderTasks,
        { provide: Reader2ApiService, useValue: api },
        { provide: ReaderConfirm, useValue: jasmine.createSpyObj<ReaderConfirm>('c', ['confirmBackFillAsync']) },
        { provide: ReaderStore, useValue: reader }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(StoryPanelComponent);
  });

  async function render(): Promise<HTMLElement> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('loads the model for where the reader is, on open and not before', async () => {
    expect(api.storyModel).not.toHaveBeenCalled();

    await render();

    expect(api.storyModel).toHaveBeenCalledWith('book-1', 4);
  });

  it('puts the open questions above everything', async () => {
    const page = await render();

    expect(page.querySelector('app-reader2-merge-resolver')).not.toBeNull();
  });

  /**
   * The round trip: a click on an answer reaches the server, and the panel then
   * shows what the server sent back — never a locally patched model, because
   * accepting fuses entries and repoints edges, and that merge lives in C#.
   */
  it('answers a question and shows the model the server returns', async () => {
    const fused = model({ actors: [actor('a1', 'Pierre', 'Major', { aliases: ['Pyotr Bezukhov'] })] });
    api.resolveMerge.and.returnValue(of(fused));

    const page = await render();
    page.querySelectorAll<HTMLButtonElement>('app-reader2-merge-resolver button')[0].click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.resolveMerge).toHaveBeenCalledWith('book-1', 'm1', true);
    expect((fixture.nativeElement as HTMLElement).querySelector('app-reader2-merge-resolver'))
      .withContext('the answered question is gone because the server no longer serves it')
      .toBeNull();
  });

  it('offers a build when nothing is ingested and summaries exist', async () => {
    api.storyModel.and.returnValue(of(model({ chaptersIngested: [] })));
    reader.chapters.set([
      { id: 0, title: 'One', level: 0, wordCount: 100, hasSummary: true },
      { id: 1, title: 'Two', level: 0, wordCount: 100, hasSummary: false }
    ]);

    const page = await render();

    expect(page.querySelector('.build')?.textContent).toContain('1 summarised chapter');
  });

  /** The button names the count and the work, so it is its own consent. */
  it('builds on the button press without asking again', async () => {
    api.storyModel.and.returnValue(of(model({ chaptersIngested: [] })));
    api.backFillStoryModel.and.returnValue(of({ kind: 'result', value: model() }) as never);
    reader.chapters.set([{ id: 0, title: 'One', level: 0, wordCount: 100, hasSummary: true }]);

    const page = await render();
    page.querySelector<HTMLButtonElement>('.build')!.click();
    await fixture.whenStable();

    expect(api.backFillStoryModel).toHaveBeenCalledWith('book-1');
  });

  it('explains instead of offering when there is nothing to build from', async () => {
    api.storyModel.and.returnValue(of(model({ chaptersIngested: [] })));

    const page = await render();

    expect(page.querySelector('.build')).toBeNull();
    expect(page.querySelector('.hint')?.textContent).toContain('Summarise a chapter first');
  });

  it('labels the cast tab with the book type’s own word', async () => {
    const tabs = Array.from((await render()).querySelectorAll('.views button'))
      .map(b => b.textContent?.trim());

    expect(tabs).toEqual(['Characters', 'Threads', 'Map']);
  });
});
