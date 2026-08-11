import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MapDetailComponent } from './map-detail.component';
import { actor, contents, edge } from '../testing/cast';

describe('MapDetailComponent', () => {
  let fixture: ComponentFixture<MapDetailComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [MapDetailComponent] }).compileComponents();
    fixture = TestBed.createComponent(MapDetailComponent);
  });

  /** Front matter first, so a chapter's index and its number disagree. */
  const CONTENTS = contents(
    'Cover', 'Copyright', 'Chapter One', 'Chapter Two', 'Chapter Three', 'Chapter Four',
    'Chapter Five', 'Chapter Six', 'Chapter Seven', 'Chapter Eight', 'Chapter Nine',
    'Chapter Ten', 'Chapter Eleven');

  function render(): HTMLElement {
    fixture.componentInstance.chapters = CONTENTS;
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('shows nothing at all until something is selected', () => {
    expect(render().querySelector('.detail')).toBeNull();
  });

  // ─── who somebody is ────────────────────────────────────────────────

  it('answers “who is this” from what is already stored', () => {
    fixture.componentInstance.actor = actor('a1', 'Eleanor', 'Secondary', {
      aliases: ['Ellie'],
      role: 'armed mountain survivor',
      dossier: 'A survivor living in the mountains who cuts Finn down from a tree.',
      arc: [{ chapter: 10, change: 'moves from suspicion to cooperation' }]
    });

    const text = render().textContent ?? '';

    expect(text).toContain('Eleanor');
    expect(text).toContain('Ellie');
    expect(text).toContain('cuts Finn down from a tree');
    expect(text).toContain('moves from suspicion to cooperation');

    // Index 10 is the book's ninth chapter — the ten front-matter and earlier
    // entries before it are why counting the index was wrong.
    expect(text).toContain('Chapter Nine');
  });

  it('says so plainly when a name is all that is recorded', () => {
    fixture.componentInstance.actor = actor('a1', 'Yatras');

    expect(render().textContent).toContain('Nothing recorded about them yet');
  });

  // ─── how two people know each other ─────────────────────────────────

  /**
   * The chapter that made two people allies and the chapter that strained it are
   * both the answer. While the edge held one overwritten string, only the last
   * of them could ever be shown.
   */
  it('answers “how do these two know each other” chapter by chapter', () => {
    fixture.componentInstance.edge = edge('a1', 'a2', 'allied', {
      sinceChapter: 4,
      notes: [
        { chapter: 4, what: 'she vouches for him at court' },
        { chapter: 8, what: 'she treats him coldly' }
      ]
    });
    fixture.componentInstance.fromName = 'Finn';
    fixture.componentInstance.toName = 'Liliana';

    const text = render().textContent ?? '';

    expect(text).toContain('Finn');
    expect(text).toContain('Liliana');
    expect(text).toContain('vouches for him at court');
    expect(text).toContain('treats him coldly');
  });

  it('says when a relationship ended rather than hiding it', () => {
    fixture.componentInstance.edge = edge('a1', 'a2', 'allied', { endedChapter: 12 });

    expect(render().textContent).toContain('Ended Chapter Eleven');
  });

  it('admits when nothing has been said about a recorded pair', () => {
    fixture.componentInstance.edge = edge('a1', 'a2', 'allied');

    expect(render().textContent).toContain('no chapter has said yet');
  });

  it('can be dismissed', () => {
    fixture.componentInstance.actor = actor('a1', 'Finn');
    let dismissed = false;
    fixture.componentInstance.dismiss.subscribe(() => (dismissed = true));

    render().querySelector<HTMLButtonElement>('.close')!.click();

    expect(dismissed).toBeTrue();
  });
});
