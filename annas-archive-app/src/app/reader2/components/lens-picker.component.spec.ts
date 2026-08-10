import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LensPickerComponent } from './lens-picker.component';
import { Lens } from '../reader2.models';

function lens(key: string, displayName: string, extra: Partial<Lens> = {}): Lens {
  return {
    key, displayName, description: `the ${displayName} reading`, icon: 'science',
    sortOrder: 0, isDefault: false, buildsStoryModel: false, storyVocabulary: null, ...extra
  };
}

/**
 * The frontend half of the extensibility guarantee.
 *
 * <p>Every case here feeds the picker a lens this file has never heard of. If
 * the component ever grows a hard-coded list, an icon map, or a switch on a key,
 * these fail — which is the point, because the server-side contract test cannot
 * see the UI.</p>
 */
describe('LensPickerComponent', () => {
  let fixture: ComponentFixture<LensPickerComponent>;
  let component: LensPickerComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [LensPickerComponent] }).compileComponents();

    fixture = TestBed.createComponent(LensPickerComponent);
    component = fixture.componentInstance;
  });

  function render(lenses: Lens[], selectedKey: string | null = null): HTMLButtonElement[] {
    component.lenses = lenses;
    component.selectedKey = selectedKey;
    fixture.detectChanges();

    return Array.from(fixture.nativeElement.querySelectorAll('button.lens'));
  }

  it('renders whatever the server returned, including a type it has never heard of', () => {
    const buttons = render([
      lens('literary', 'Ideas'),
      lens('naval-history', 'Naval History')
    ]);

    expect(buttons.length).toBe(2);
    expect(buttons[1].textContent).toContain('Naval History');
  });

  it('renders no options at all rather than inventing one when the list is empty', () => {
    expect(render([]).length).toBe(0);
  });

  it('marks the current book type for assistive technology, not just visually', () => {
    const buttons = render([lens('literary', 'Ideas'), lens('fiction', 'Story')], 'fiction');

    expect(buttons[0].getAttribute('aria-checked')).toBe('false');
    expect(buttons[1].getAttribute('aria-checked')).toBe('true');
    expect(buttons[1].classList).toContain('selected');
  });

  it('shows the server’s description as the tooltip', () => {
    expect(render([lens('naval-history', 'Naval History')])[0].title)
      .toBe('the Naval History reading');
  });

  it('emits the key the server gave it, never an index or a label', () => {
    let chosen = "";
    component.choose.subscribe((key: string) => { chosen = key; });

    render([lens('literary', 'Ideas'), lens('naval-history', 'Naval History')])[1].click();

    expect(chosen).toBe('naval-history');
  });

  it('cannot be used while the reader is waiting', () => {
    component.disabled = true;
    const buttons = render([lens('literary', 'Ideas')]);

    expect(buttons[0].disabled).toBeTrue();
  });
});
