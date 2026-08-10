import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AuthService } from '../../services/auth.service';
import { NAV_ENTRIES, NavEntry } from './nav-model';

/**
 * The app's navigation, rendered from NAV_ENTRIES in two shapes.
 *
 * **Expanded** — full width, full labels. Groups render as a header with their
 * children indented beneath, open by default: the point of expanding is to see
 * everything at once, so making someone click each group again would undo it.
 *
 * **Rail** (`collapsed`) — a narrow strip listing every *destination* as an
 * icon plus a short caption, so it stays usable rather than becoming a row of
 * anonymous glyphs. Groups are flattened away here rather than opening a
 * flyout: a rail exists to reach a page in one click, and a menu would cost
 * two. That's why children carry their own `shortLabel`.
 *
 * One component serves the permanent sidebar and the phone drawer, so there is
 * only ever one nav implementation to keep in step with the menu.
 */
@Component({
  selector: 'app-sidebar-nav',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, MatIconModule, MatTooltipModule],
  template: `
    <!-- One definition of "an entry's icon", so a badged icon renders identically
         in the rail, the expanded list and a group's children. -->
    <ng-template #icon let-entry>
      <span class="nav-icon-stack">
        <mat-icon class="nav-icon">{{ entry.icon }}</mat-icon>
        <mat-icon *ngIf="entry.overlayIcon" class="nav-icon-badge">{{ entry.overlayIcon }}</mat-icon>
      </span>
    </ng-template>

    <nav class="sidebar-nav" [class.rail]="collapsed" [class.dark]="dark" aria-label="Main navigation">

      <!-- ── Rail: one icon per destination, groups flattened away ────────
           No flyouts. A rail exists to reach a page in one click; making a
           group open a menu first would cost two. -->
      <ng-container *ngIf="collapsed">
        <a
          *ngFor="let entry of railEntries"
          class="nav-link"
          [routerLink]="entry.route"
          routerLinkActive="active"
          [routerLinkActiveOptions]="{ exact: true }"
          [matTooltip]="entry.label"
          matTooltipPosition="right"
          (click)="navigated.emit()">
          <ng-container *ngTemplateOutlet="icon; context: { $implicit: entry }"></ng-container>
          <span class="nav-text">{{ entry.shortLabel ?? entry.label }}</span>
        </a>
      </ng-container>

      <!-- ── Expanded: links, plus groups open by default ─────────────── -->
      <ng-container *ngIf="!collapsed">
        <ng-container *ngFor="let entry of visibleEntries">
          <a
            *ngIf="entry.route"
            class="nav-link"
            [routerLink]="entry.route"
            routerLinkActive="active"
            [routerLinkActiveOptions]="{ exact: true }"
            (click)="navigated.emit()">
            <ng-container *ngTemplateOutlet="icon; context: { $implicit: entry }"></ng-container>
            <span class="nav-text">{{ entry.label }}</span>
          </a>

          <ng-container *ngIf="entry.children">
            <button
              type="button"
              class="nav-link nav-group"
              [attr.aria-expanded]="isOpen(entry)"
              (click)="toggle(entry)">
              <ng-container *ngTemplateOutlet="icon; context: { $implicit: entry }"></ng-container>
              <span class="nav-text">{{ entry.label }}</span>
              <mat-icon class="nav-chevron">{{ isOpen(entry) ? 'expand_more' : 'chevron_right' }}</mat-icon>
            </button>

            <a
              *ngFor="let child of isOpen(entry) ? visibleChildren(entry) : []"
              class="nav-link nav-child"
              [routerLink]="child.route"
              routerLinkActive="active"
              [routerLinkActiveOptions]="{ exact: true }"
              (click)="navigated.emit()">
              <ng-container *ngTemplateOutlet="icon; context: { $implicit: child }"></ng-container>
              <span class="nav-text">{{ child.label }}</span>
            </a>
          </ng-container>
        </ng-container>
      </ng-container>
    </nav>
  `,
  styles: [`
    .sidebar-nav {
      display: flex;
      flex-direction: column;
      padding: 8px 0;
      min-width: 0;
    }

    /* One row style for links, group headers and children alike, so the two
       layouts differ only by direction and spacing rather than by having
       separate rules that can drift. */
    .nav-link {
      display: flex;
      align-items: center;
      gap: 14px;
      width: 100%;
      padding: 0 16px;
      min-height: 48px;
      border: none;
      background: none;
      font: inherit;
      color: #3c4043;
      text-align: left;
      text-decoration: none;
      cursor: pointer;
      border-left: 3px solid transparent;
      white-space: nowrap;
    }
    .nav-link:hover { background: rgba(0, 0, 0, 0.04); }

    /* The stack owns the row's layout slot so the badge, which is positioned out
       of flow, cannot change how much width the icon takes. */
    .nav-icon-stack {
      position: relative;
      flex: 0 0 auto;
      display: inline-flex;
      width: 24px;
      height: 24px;
    }

    .nav-icon { flex: 0 0 auto; color: #5f6368; }

    /* Bottom-right, with a ring in the sidebar's own background colour so the
       badge reads as sitting on top of the book rather than merged into it. */
    .nav-icon-badge {
      position: absolute;
      right: -5px;
      bottom: -4px;
      width: 15px;
      height: 15px;
      font-size: 15px;
      line-height: 15px;
      border-radius: 50%;
      background: #fff;
      box-shadow: 0 0 0 1.5px #fff;
      color: #5f6368;
    }

    .nav-text { flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; }

    .nav-group { font-weight: 600; color: #1f2937; }
    .nav-chevron { flex: 0 0 auto; color: #9aa0a6; font-size: 20px; width: 20px; height: 20px; }

    /* Children sit under their group's label, not its icon. */
    .nav-child { padding-left: 46px; }

    .nav-link.active {
      background: rgba(63, 81, 181, 0.10);
      border-left-color: #3f51b5;
      color: #3f51b5;
      font-weight: 600;
    }
    .nav-link.active .nav-icon,
    .nav-link.active .nav-icon-badge { color: #3f51b5; }

    /* ── Rail ──────────────────────────────────────────────────────────────
       Icon over a short caption, centred, in a strip narrow enough that the
       page keeps essentially all of its width. */

    .sidebar-nav.rail .nav-link {
      flex-direction: column;
      justify-content: center;
      gap: 4px;
      padding: 8px 2px;
      min-height: 60px;
      border-left: none;
      border-right: 3px solid transparent;
      text-align: center;
    }

    .sidebar-nav.rail .nav-text {
      flex: 0 0 auto;
      font-size: 11px;
      line-height: 1.15;
      max-width: 100%;
      /* Captions are up to two words, so they wrap rather than truncate. */
      white-space: normal;
      overflow: visible;
    }

    .sidebar-nav.rail .nav-link.active {
      background: rgba(63, 81, 181, 0.10);
      border-right-color: #3f51b5;
      color: #3f51b5;
    }
    .sidebar-nav.rail .nav-link.active .nav-icon,
    .sidebar-nav.rail .nav-link.active .nav-icon-badge { color: #3f51b5; }


    /* ── Date Night ──────────────────────────────────────────────────────────
       Those pages are full-bleed black, so a light sidebar beside them reads as
       a rendering fault rather than a design. Colours are the theater palette
       from styles/theater.scss (gilt frame, cream type, shock red accent), so
       the nav looks like part of the same room instead of a second theme. */

    .sidebar-nav.dark .nav-link { color: var(--thtr-parchment, #e8dcc0); }
    .sidebar-nav.dark .nav-link:hover { background: rgba(255, 238, 201, 0.08); }
    .sidebar-nav.dark .nav-icon { color: var(--thtr-gilt, #d9a441); }

    /* The badge's ring exists to separate it from the icon beneath, so on the
       black theater pages it has to be the black — a white ring would read as a
       rendering fault. */
    .sidebar-nav.dark .nav-icon-badge {
      color: var(--thtr-gilt, #d9a441);
      background: #000;
      box-shadow: 0 0 0 1.5px #000;
    }
    .sidebar-nav.dark .nav-group { color: var(--thtr-cream, #ffeec9); }
    .sidebar-nav.dark .nav-chevron { color: rgba(217, 164, 65, 0.7); }

    .sidebar-nav.dark .nav-link.active {
      background: rgba(217, 164, 65, 0.16);
      border-left-color: var(--thtr-gilt-bright, #ffdf7e);
      color: var(--thtr-gilt-bright, #ffdf7e);
    }
    .sidebar-nav.dark .nav-link.active .nav-icon,
    .sidebar-nav.dark .nav-link.active .nav-icon-badge { color: var(--thtr-gilt-bright, #ffdf7e); }

    .sidebar-nav.dark.rail .nav-link.active {
      background: rgba(217, 164, 65, 0.16);
      border-right-color: var(--thtr-gilt-bright, #ffdf7e);
      color: var(--thtr-gilt-bright, #ffdf7e);
    }
    .sidebar-nav.dark.rail .nav-link.active .nav-icon,
    .sidebar-nav.dark.rail .nav-link.active .nav-icon-badge {
      color: var(--thtr-gilt-bright, #ffdf7e);
    }
  `]
})
export class SidebarNavComponent {
  /** Renders the icon rail instead of the full-width panel. */
  @Input() collapsed = false;

  /** Matches the sidebar to the Date Night pages' black background. */
  @Input() dark = false;

  /** Lets the phone drawer close itself once a destination is chosen. The
   *  permanent sidebar ignores it. */
  @Output() navigated = new EventEmitter<void>();

  readonly entries = NAV_ENTRIES;

  /** Groups the person has deliberately collapsed. Absence means open, so
   *  groups start expanded without needing to be seeded per render. */
  private closed = new Set<string>();

  // Highlighting is `routerLinkActive`'s job, in the template. This class used
  // to also hold a `router.events` subscription that tracked the current URL
  // into a private field — which nothing ever read. Removing the field took
  // the subscription, the Router, and the OnDestroy hook with it.
  constructor(private auth: AuthService) {}

  /** Entries this person can actually reach — a group whose children are all
   *  admin-only would otherwise render as an empty expandable. */
  get visibleEntries(): NavEntry[] {
    return this.entries.filter(e =>
      e.children ? this.visibleChildren(e).length > 0 : this.canSee(e)
    );
  }

  visibleChildren(entry: NavEntry): NavEntry[] {
    return (entry.children ?? []).filter(c => this.canSee(c));
  }

  /** Every destination as a flat list — the rail shows leaves only, so a group
   *  contributes its children rather than itself. */
  get railEntries(): NavEntry[] {
    return this.entries.flatMap(e => (e.children ? this.visibleChildren(e) : this.canSee(e) ? [e] : []));
  }

  isOpen(entry: NavEntry): boolean {
    return !this.closed.has(entry.label);
  }

  toggle(entry: NavEntry): void {
    if (!this.closed.delete(entry.label)) this.closed.add(entry.label);
  }

  private canSee(entry: NavEntry): boolean {
    if (entry.adminOnly && !this.auth.isAdmin()) return false;

    return !(entry.nonAdminOnly && this.auth.isAdmin());
  }
}
