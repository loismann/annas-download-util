import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PlaceListComponent } from './place-list.component';
import { Place } from '../reader2.models';
import { contents } from '../testing/cast';

function place(id: string, name: string, extra?: Partial<Place>): Place {
  return {
    id, name, aliases: [], kind: 'Settlement', description: '', partOf: '',
    firstSeenChapter: 0, lastSeenChapter: 0, ...extra
  };
}

/**
 * Where the book has been.
 *
 * <p>A flat list of forty names answers nothing. The question a reader has is
 * "where was that", and the only useful answer is the chain upward — so what is
 * worth testing here is the nesting, and that nothing falls out of it.</p>
 */
describe('PlaceListComponent', () => {
  let fixture: ComponentFixture<PlaceListComponent>;

  const CONTENTS = contents('Cover', 'Copyright', 'Chapter One', 'Chapter Two');

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [PlaceListComponent] }).compileComponents();
    fixture = TestBed.createComponent(PlaceListComponent);
  });

  function render(places: Place[]): HTMLElement {
    fixture.componentRef.setInput('places', places);
    fixture.componentRef.setInput('chapters', CONTENTS);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function names(): string[] {
    return Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('.name'), n => n.textContent!.trim());
  }

  it('says so plainly when nowhere is recorded yet', () => {
    expect(render([]).textContent).toContain('No places recorded yet');
  });

  it('shows what a place is and what it is like', () => {
    const text = render([
      place('p1', 'Ravensmarch', { description: 'A river capital.', aliases: ['the Marches'] })
    ]).textContent ?? '';

    expect(text).toContain('Ravensmarch');
    expect(text).toContain('A river capital.');
    expect(text).toContain('the Marches');
  });

  it('names the chapter the way the contents list names it', () => {
    expect(render([place('p1', 'Ravensmarch', { firstSeenChapter: 2 })]).textContent)
      .toContain('Chapter One');
  });

  // ─── the nesting ────────────────────────────────────────────────────

  it('puts a place under whatever contains it', () => {
    render([
      place('p1', 'The Gate House', { kind: 'Building', partOf: 'p2' }),
      place('p2', 'Ravensmarch')
    ]);

    expect(names()).toEqual(['Ravensmarch', 'The Gate House']);
  });

  it('nests a chain of three', () => {
    render([
      place('p1', 'The Cellar', { kind: 'Building', partOf: 'p2' }),
      place('p2', 'The Gate House', { kind: 'Building', partOf: 'p3' }),
      place('p3', 'Ravensmarch')
    ]);

    expect(names()).toEqual(['Ravensmarch', 'The Gate House', 'The Cellar']);
  });

  it('treats a container it has not been given as no container at all', () => {
    render([place('p1', 'The Gate House', { kind: 'Building', partOf: 'p99' })]);

    expect(names()).toEqual(['The Gate House']);
  });

  /**
   * The merge refuses cycles, but this renders whatever the server sent — and a
   * list that hangs the panel is worse than one that indents something oddly.
   */
  it('shows every place even when two of them contain each other', () => {
    render([
      place('p1', 'Ravensmarch', { partOf: 'p2' }),
      place('p2', 'The Gate House', { kind: 'Building', partOf: 'p1' })
    ]);

    expect(names().sort()).toEqual(['Ravensmarch', 'The Gate House']);
  });

  it('lists a place exactly once', () => {
    render([
      place('p1', 'Ravensmarch'),
      place('p2', 'The Gate House', { kind: 'Building', partOf: 'p1' }),
      place('p3', 'The Cellar', { kind: 'Building', partOf: 'p1' })
    ]);

    expect(names().length).toBe(3);
  });
});
