import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FlashcardPanelComponent } from './flashcard-panel.component';
import { Flashcard } from '../reader2.models';

function card(term: string): Flashcard {
  return { term, definition: `what ${term} means`, addedAtUtc: '', norm: term.toLowerCase() };
}

describe('FlashcardPanelComponent', () => {
  let fixture: ComponentFixture<FlashcardPanelComponent>;
  let component: FlashcardPanelComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [FlashcardPanelComponent] })
      .compileComponents();

    fixture = TestBed.createComponent(FlashcardPanelComponent);
    component = fixture.componentInstance;
  });

  function render(cards: Flashcard[]): HTMLElement {
    fixture.componentRef.setInput('cards', cards);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('shows the deck and its size', () => {
    const host = render([card('praxis'), card('aporia')]);

    expect(host.querySelectorAll('.deck li').length).toBe(2);
    expect(host.querySelector('.count')?.textContent?.trim()).toBe('2');
  });

  /** A card face-down is the whole point; showing both sides is not a test. */
  it('hides the definition until the card is turned', () => {
    const host = render([card('praxis')]);
    expect(host.querySelector('.back')).toBeNull();

    host.querySelector<HTMLButtonElement>('.card')!.click();
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();

    expect(host.querySelector('.back')?.textContent).toContain('what praxis means');
  });

  it('turns the card back over when it is pressed again', () => {
    const host = render([card('praxis')]);
    const button = host.querySelector<HTMLButtonElement>('.card')!;

    button.click();
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
    button.click();
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();

    expect(host.querySelector('.back')).toBeNull();
  });

  /** Two cards face-up at once would give away the answer the reader is on. */
  it('shows only one card face-up at a time', () => {
    const host = render([card('praxis'), card('aporia')]);
    const [first, second] = Array.from(host.querySelectorAll<HTMLButtonElement>('.card'));

    first.click();
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
    second.click();
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();

    expect(host.querySelectorAll('.back').length).toBe(1);
    expect(host.querySelector('.back')?.textContent).toContain('aporia');
  });

  it('removes a card by its term, which is what the server keys on', () => {
    let removed = '';
    component.remove.subscribe((t: string) => { removed = t; });

    render([card('praxis')]).querySelector<HTMLButtonElement>('.remove')!.click();

    expect(removed).toBe('praxis');
  });

  it('offers no clear control for an empty deck', () => {
    expect(render([]).querySelector('.clear')).toBeNull();
    expect(render([]).querySelector('.idle')).not.toBeNull();
  });
});
