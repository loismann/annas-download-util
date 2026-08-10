import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SidebarNavComponent } from './sidebar-nav.component';
import { NAV_ENTRIES, NavEntry } from './nav-model';
import { AuthService } from '../../services/auth.service';

/**
 * Characterization tests for the app navigation.
 *
 * A review pass in the same series as the library pages. It found the class
 * holding a `router.events` subscription whose only job was to keep a private
 * `activeUrl` field up to date — a field nothing read, since highlighting is
 * `routerLinkActive`'s job in the template. Removing it took the Router and the
 * OnDestroy hook with it; the tests below pin the behaviour that survived.
 *
 * The admin filtering gets the most attention because the rail and the expanded
 * panel derive their lists separately, and an entry leaking into one but not the
 * other is exactly the kind of drift a single NAV_ENTRIES source is meant to
 * prevent.
 */
describe('SidebarNavComponent (characterization)', () => {
  let fixture: ComponentFixture<SidebarNavComponent>;
  let component: SidebarNavComponent;
  let isAdmin: boolean;

  /** Every leaf destination in the model, admin-only ones included. */
  function allLeaves(entries: NavEntry[] = NAV_ENTRIES): NavEntry[] {
    return entries.flatMap(e => (e.children ? allLeaves(e.children) : [e]));
  }

  beforeEach(async () => {
    isAdmin = false;

    await TestBed.configureTestingModule({
      imports: [SidebarNavComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { isAdmin: () => isAdmin } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SidebarNavComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  // ─── The model ───────────────────────────────────────────────────────

  describe('the model', () => {
    it('should give every entry a label and an icon', () => {
      const missing = allLeaves().filter(e => !e.label || !e.icon);

      expect(missing).toEqual([]);
    });

    it('should make an entry either a link or a group, never both', () => {
      // The template branches on exactly this, so an entry with both would
      // render twice and one with neither would render as a dead row.
      const wrong = allLeaves(NAV_ENTRIES).filter(e => !e.route === !e.children);

      expect(wrong.map(e => e.label)).toEqual([]);
    });

    it('should give every rail destination a caption', () => {
      // The rail flattens groups away, so every leaf shows up there on its own
      // and falls back to `label` — which is what the shortLabel exists to
      // avoid for the long ones.
      const captionless = component.railEntries.filter(e => !(e.shortLabel ?? e.label));

      expect(captionless).toEqual([]);
    });

    it('should not route two entries to the same place', () => {
      const routes = allLeaves().map(e => e.route).filter(Boolean);

      expect(routes.length).toBe(new Set(routes).size);
    });
  });

  // ─── Who can see what ────────────────────────────────────────────────

  describe('visibility', () => {
    // Keyed by route rather than label: the two readers deliberately share the
    // label "Ebook Reader", so a route is the only honest identity here.
    it('should hide admin-only entries from everyone else', () => {
      const shown = component.railEntries.map(e => e.route);
      const adminOnly = allLeaves().filter(e => e.adminOnly).map(e => e.route);

      expect(adminOnly.length).toBeGreaterThan(0);
      adminOnly.forEach(route => expect(shown).not.toContain(route));
    });

    it('should show them to an admin', () => {
      isAdmin = true;
      const adminOnly = allLeaves().filter(e => e.adminOnly).map(e => e.route);

      const shown = component.railEntries.map(e => e.route);

      adminOnly.forEach(route => expect(shown).toContain(route));
    });

    // ─── the reader split ────────────────────────────────────────────

    it('should give the family the original reader and not Reader II', () => {
      const routes = component.railEntries.map(e => e.route);

      expect(routes).toContain('/reader');
      expect(routes).not.toContain('/reader2');
    });

    it('should give the admin Reader II and not the original', () => {
      isAdmin = true;

      const routes = component.railEntries.map(e => e.route);

      expect(routes).toContain('/reader2');
      expect(routes).not.toContain('/reader');
    });

    it('should call each person\'s reader by the same plain name', () => {
      const family = component.railEntries.find(e => e.route === '/reader');
      isAdmin = true;
      const admin = component.railEntries.find(e => e.route === '/reader2');

      expect(family?.label).toBe('Ebook Reader');
      expect(admin?.label).toBe('Ebook Reader');
    });

    it('should filter the rail and the panel identically', () => {
      // The two lists are derived separately. An entry visible in one and not
      // the other is the drift a single source of truth is meant to prevent.
      const railLabels = new Set(component.railEntries.map(e => e.label));
      const panelLeaves = component.visibleEntries.flatMap(
        e => (e.children ? component.visibleChildren(e) : [e]));

      expect(panelLeaves.map(e => e.label).sort()).toEqual([...railLabels].sort());
    });

    it('should drop a group whose children are all admin-only', () => {
      // Otherwise it renders as an expandable that opens onto nothing.
      const empty = component.visibleEntries.filter(
        e => e.children && component.visibleChildren(e).length === 0);

      expect(empty).toEqual([]);
    });
  });

  // ─── Groups ──────────────────────────────────────────────────────────

  describe('groups', () => {
    /** The first entry in the model that is actually a group. */
    const group = (): NavEntry => NAV_ENTRIES.find(e => !!e.children)!;

    it('should start open', () => {
      // The point of expanding the sidebar is to see everything at once.
      expect(component.isOpen(group())).toBe(true);
    });

    it('should close and reopen on the header', () => {
      component.toggle(group());
      expect(component.isOpen(group())).toBe(false);

      component.toggle(group());
      expect(component.isOpen(group())).toBe(true);
    });

    it('should close one group without closing the others', () => {
      const groups = NAV_ENTRIES.filter(e => e.children);
      expect(groups.length).toBeGreaterThan(1);

      component.toggle(groups[0]);

      expect(component.isOpen(groups[0])).toBe(false);
      groups.slice(1).forEach(g => expect(component.isOpen(g)).toBe(true));
    });
  });

  // ─── Rendering ───────────────────────────────────────────────────────

  describe('rendering', () => {
    it('should render one link per destination in the rail', () => {
      component.collapsed = true;
      fixture.detectChanges();

      const links = fixture.nativeElement.querySelectorAll('a.nav-link');
      expect(links.length).toBe(component.railEntries.length);
      expect(fixture.nativeElement.querySelector('.sidebar-nav.rail')).toBeTruthy();
    });

    it('should render group headers only when expanded', () => {
      // The rail has no flyouts, so a group there would be a button that
      // reaches nothing.
      component.collapsed = true;
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelectorAll('button.nav-group').length).toBe(0);

      component.collapsed = false;
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelectorAll('button.nav-group').length)
        .toBe(component.visibleEntries.filter(e => e.children).length);
    });

    it('should hide a group\'s children once it is closed', () => {
      fixture.detectChanges();
      const before = fixture.nativeElement.querySelectorAll('a.nav-child').length;

      component.toggle(NAV_ENTRIES.find(e => !!e.children)!);
      fixture.detectChanges();

      const after = fixture.nativeElement.querySelectorAll('a.nav-child').length;
      expect(after).toBeLessThan(before);
    });

    it('should say whether a group is open for a screen reader', () => {
      fixture.detectChanges();
      const header = fixture.nativeElement.querySelector('button.nav-group') as HTMLElement;
      expect(header.getAttribute('aria-expanded')).toBe('true');

      component.toggle(component.visibleEntries.find(e => e.children)!);
      fixture.detectChanges();

      expect(header.getAttribute('aria-expanded')).toBe('false');
    });

    it('should draw a badge only where the model asks for one', () => {
      // The icon font has no single "audiobook" glyph, so that one entry
      // composes a book plus headphones rather than settling for a wrong icon.
      component.collapsed = true;
      fixture.detectChanges();

      const badges = fixture.nativeElement.querySelectorAll('.nav-icon-badge');
      expect(badges.length).toBe(component.railEntries.filter(e => e.overlayIcon).length);
      expect(badges.length).toBeGreaterThan(0);
    });

    it('should take the theater palette on the dark pages', () => {
      // The Date Night pages are full-bleed black; a light sidebar beside them
      // reads as a rendering fault.
      component.dark = true;
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.sidebar-nav.dark')).toBeTruthy();
    });

    it('should announce itself to assistive tech', () => {
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('nav')?.getAttribute('aria-label'))
        .toBe('Main navigation');
    });
  });

  // ─── Closing the phone drawer ────────────────────────────────────────

  describe('choosing a destination', () => {
    it('should report a navigation so the phone drawer can close itself', () => {
      const navigated = jasmine.createSpy('navigated');
      component.navigated.subscribe(navigated);
      fixture.detectChanges();

      (fixture.nativeElement.querySelector('a.nav-link') as HTMLElement).click();

      expect(navigated).toHaveBeenCalled();
    });

    it('should not report one for opening a group', () => {
      // Expanding a group is not going anywhere — closing the drawer on it
      // would take the menu away mid-choice.
      const navigated = jasmine.createSpy('navigated');
      component.navigated.subscribe(navigated);
      fixture.detectChanges();

      (fixture.nativeElement.querySelector('button.nav-group') as HTMLElement).click();

      expect(navigated).not.toHaveBeenCalled();
    });
  });
});
