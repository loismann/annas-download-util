import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

/**
 * Guards the one rule in styles/library-layout.scss that a unit test can
 * actually see fail: the mobile filter FAB must be hidden on desktop.
 *
 * This is not a paranoid test. It regressed in production. The FAB is a
 * `<button mat-icon-button>`, and Angular Material declares MatIconButton with
 * ViewEncapsulation.None, so `.mat-mdc-icon-button { display: inline-block }`
 * lands in <head> when the button module loads — after the global stylesheet.
 * A `.mobile-sidebar-toggle { display: none }` written class-only ties on
 * specificity and loses on source order, and the X appeared on desktop for
 * every page that did not happen to also carry a component-scoped copy.
 *
 * Reproducing that needs the real cascade, which is exactly what karma has:
 * angular.json loads src/styles.scss into the test bundle, and importing
 * MatButtonModule below injects Material's styles the same way the app does.
 * A source-scanning assertion would not have caught it — the rule was present
 * and correct-looking the whole time; it was simply outranked.
 *
 * Note which assertion is load-bearing. Karma's headless window is phone-sized,
 * so the media query matches and the desktop `display: none` branch is never
 * the one CI runs. The guard that actually kills the regression is the
 * width-independent "never inline-block": under the old selector this suite
 * reported `Expected 'inline-block' not to be 'inline-block'`, which is the
 * production bug stated as a test failure.
 */
@Component({
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  template: `
    <button class="mobile-sidebar-toggle" mat-icon-button aria-label="Show filters">
      <mat-icon>filter_list</mat-icon>
    </button>
    <div class="sidebar-backdrop"></div>
  `,
})
class FabHostComponent {}

describe('library-layout global stylesheet', () => {
  let fixture: ComponentFixture<FabHostComponent>;

  /** True when karma's browser window is phone-sized, matching the partial. */
  const isPhoneViewport = () => window.matchMedia('(max-width: 768px)').matches;

  const displayOf = (selector: string): string => {
    const el = fixture.nativeElement.querySelector(selector) as HTMLElement;
    return getComputedStyle(el).display;
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [FabHostComponent] }).compileComponents();
    fixture = TestBed.createComponent(FabHostComponent);
    fixture.detectChanges();
  });

  it('hides the filter FAB on desktop and shows it as a flex FAB on phones', () => {
    // Either way the answer must come from our stylesheet, never from
    // Material's inline-block default — that value is the bug's signature.
    expect(displayOf('.mobile-sidebar-toggle')).toBe(isPhoneViewport() ? 'flex' : 'none');
  });

  it('never lets Material win the display declaration on the FAB', () => {
    expect(displayOf('.mobile-sidebar-toggle')).not.toBe('inline-block');
  });

  it('hides the scrim on desktop', () => {
    expect(displayOf('.sidebar-backdrop')).toBe(isPhoneViewport() ? 'block' : 'none');
  });
});

/**
 * Guards the bulk-edit cluster, which moved here from four component
 * stylesheets that each held a byte-identical copy.
 *
 * The failure this exists for is the silent one: a global rule reaches a
 * component's markup only because it is global, and nothing in the build
 * complains if it stops arriving. Delete the block from library-layout.scss
 * and four sidebars render unstyled with a green compile.
 *
 * MatButtonModule is imported for the same reason as the FAB suite above —
 * `.bulk-edit-toggle` is a mat-button in every real caller, so Material's
 * ViewEncapsulation.None styles must be in the cascade for the width
 * assertion to mean anything.
 */
@Component({
  standalone: true,
  imports: [MatButtonModule],
  template: `
    <div class="bulk-host" style="width: 200px">
      <button class="bulk-edit-toggle" mat-button>Bulk edit</button>
      <div class="bulk-edit-controls">
        <span class="selection-counter">3 selected</span>
      </div>
    </div>
  `,
})
class BulkEditHostComponent {}

describe('library-layout bulk-edit cluster', () => {
  let fixture: ComponentFixture<BulkEditHostComponent>;

  const styleOf = (selector: string): CSSStyleDeclaration =>
    getComputedStyle(fixture.nativeElement.querySelector(selector) as HTMLElement);

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [BulkEditHostComponent] }).compileComponents();
    fixture = TestBed.createComponent(BulkEditHostComponent);
    fixture.detectChanges();
  });

  it('stacks the bulk-edit controls in a column', () => {
    const style = styleOf('.bulk-edit-controls');
    expect(style.display).toBe('flex');
    expect(style.flexDirection).toBe('column');
  });

  it('centres the selection counter and keeps it bold', () => {
    const style = styleOf('.selection-counter');
    expect(style.textAlign).toBe('center');
    expect(style.fontWeight).toBe('600');
  });

  it('stretches the toggle to its container rather than Material\'s intrinsic width', () => {
    expect(styleOf('.bulk-edit-toggle').width).toBe('200px');
  });
});
