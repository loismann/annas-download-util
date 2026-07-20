import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { SpotifinatorComponent } from './spotifinator.component';

describe('SpotifinatorComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SpotifinatorComponent, NoopAnimationsModule],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    }).compileComponents();
  });

  it('should create', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    expect(component).toBeTruthy();
  });

  it('should render the chat card', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const card = compiled.querySelector('.chat-card');
    expect(card).toBeTruthy();
  });

  it('should have a welcome message on init', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    expect(component.messages.length).toBe(1);
    expect(component.messages[0].role).toBe('assistant');
  });

  it('should render the input area', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const inputArea = compiled.querySelector('.input-area');
    expect(inputArea).toBeTruthy();
  });

  it('should have idle viewState initially', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    expect(component.viewState).toBe('idle');
  });

  it('should not submit empty messages', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    component.userInput = '   ';
    component.onSubmit();
    // Should still only have the welcome message
    expect(component.messages.length).toBe(1);
  });

  it('should format duration correctly', () => {
    const fixture = TestBed.createComponent(SpotifinatorComponent);
    const component = fixture.componentInstance;
    expect(component.formatDuration(180000)).toBe('3:00');
    expect(component.formatDuration(65000)).toBe('1:05');
    expect(component.formatDuration(30000)).toBe('0:30');
  });
});
