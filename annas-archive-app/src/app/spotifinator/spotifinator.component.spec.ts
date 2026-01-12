import { TestBed } from '@angular/core/testing';
import { SpotifinatorComponent } from './spotifinator.component';

/**
 * Unit tests for SpotifinatorComponent
 * Verifies the spotifinator component renders correctly
 */
describe('SpotifinatorComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SpotifinatorComponent]
    }).compileComponents();
  });

  it('should render the title', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const title = compiled.querySelector('.coming-soon-title');
    expect(title).toBeTruthy();
    expect(title?.textContent).toContain('Spotif-inator');
  });

  it('should render the subtitle', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const subtitle = compiled.querySelector('.coming-soon-subtitle');
    expect(subtitle).toBeTruthy();
    expect(subtitle?.textContent).toContain('Admin Feature');
  });

  it('should render loading dots animation', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const dots = compiled.querySelectorAll('.dot');
    expect(dots.length).toBe(3);
  });

  it('should use Material Design styling (light background)', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const container = compiled.querySelector('.spotifinator-container');
    expect(container).toBeTruthy();

    const containerStyles = window.getComputedStyle(container as Element);
    // Verify it's not using purple gradient (should be #fafafa or similar light color)
    expect(containerStyles.background).not.toContain('667eea');
    expect(containerStyles.background).not.toContain('764ba2');
  });

  it('should render content card with proper styling', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const contentCard = compiled.querySelector('.content-card');
    expect(contentCard).toBeTruthy();
  });
});
