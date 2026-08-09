import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

/**
 * Guards styles/_tokens.scss.
 *
 * 888 declarations across 45 stylesheets now read `var(--slate-600)` instead of
 * `#475569`. That trade is only worth making if the indirection is reliable, and
 * two things could silently break it:
 *
 *   1. A token loses its definition. Every one of its call sites then falls back
 *      to nothing — the property simply does not apply, with no build error and
 *      no console warning. Text renders in the inherited colour and looks
 *      plausible, which is the worst kind of regression.
 *   2. The definition stops reaching component styles. The whole approach rests
 *      on custom properties inheriting through the DOM rather than being scoped
 *      the way Angular scopes a component stylesheet. If that assumption were
 *      wrong, every component would lose its colours at once.
 *
 * Karma can see both, for the same reason library-layout.spec.ts can see the FAB
 * regression: angular.json loads src/styles.scss into the test bundle, so the
 * real cascade is present.
 */
describe('colour tokens', () => {
  /** Resolved value of a custom property on :root, trimmed. */
  const token = (name: string): string =>
    getComputedStyle(document.documentElement).getPropertyValue(name).trim();

  /**
   * Every custom property our own stylesheets declare on :root.
   *
   * Read from the live cascade rather than a hand-kept list, so a token added to
   * _tokens.scss is covered here the moment it exists. Cross-origin sheets throw
   * on .cssRules and are skipped; Material's prebuilt theme is one of ours only
   * incidentally, so `--mat-*`/`--mdc-*` are filtered out below.
   */
  function declaredRootProperties(): string[] {
    const names = new Set<string>();

    for (const sheet of Array.from(document.styleSheets)) {
      let rules: CSSRule[];
      try {
        rules = Array.from(sheet.cssRules);
      } catch {
        continue;
      }

      for (const rule of rules) {
        if (!(rule instanceof CSSStyleRule) || !/(^|,)\s*:root\s*(,|$)/.test(rule.selectorText)) {
          continue;
        }
        for (const prop of Array.from(rule.style)) {
          // `--mdc-*` and Material's own `--mat-sys-*` come from the prebuilt
          // theme, not from us. Ours are `--mat-red-500`-style: one segment.
          if (prop.startsWith('--') && !prop.startsWith('--mdc-') && !prop.startsWith('--mat-sys-')) {
            names.add(prop);
          }
        }
      }
    }

    return [...names];
  }

  it('should declare the palette on :root', () => {
    // Cheap canary: if styles.scss ever stops pulling in the partial, every
    // assertion below would pass vacuously on an empty list.
    expect(declaredRootProperties().length).toBeGreaterThan(50);
  });

  it('should give every declared token a value', () => {
    const empty = declaredRootProperties().filter(name => token(name) === '');

    expect(empty).toEqual([]);
  });

  // ─── Values ──────────────────────────────────────────────────────────

  /**
   * The high-traffic tokens, pinned by value. Not every token — that would just
   * restate the stylesheet — but the ones where a typo would repaint a large
   * part of the app before anyone noticed.
   */
  const PINNED: ReadonlyArray<readonly [string, string]> = [
    ['--brand', 'rgb(63, 81, 181)'],
    ['--white', 'rgb(255, 255, 255)'],
    ['--slate-50', 'rgb(248, 250, 252)'],
    ['--slate-500', 'rgb(100, 116, 139)'],
    ['--slate-600', 'rgb(71, 85, 105)'],
    ['--slate-900', 'rgb(15, 23, 42)'],
  ];

  PINNED.forEach(([name, expected]) => {
    it(`should hold ${name} steady`, () => {
      // Compared as rendered colour rather than as the literal source text, so
      // the assertion does not care whether the file says #fff or #ffffff.
      const probe = document.createElement('div');
      probe.style.color = `var(${name})`;
      document.body.appendChild(probe);

      try {
        expect(getComputedStyle(probe).color).toBe(expected);
      } finally {
        probe.remove();
      }
    });
  });

  it('should have exactly one neutral ramp', () => {
    // Two others used to shadow slate: Tailwind's `gray` (drift, three RGB
    // points apart, merged at 62 sites) and a pre-Tailwind pure-grey ramp
    // (`--grey-66` and friends — visibly a different neutral, merged at 107
    // sites once that was a deliberate decision rather than a tidy-up).
    //
    // Neither comes back. A third neutral is how the first two happened.
    const neutrals = declaredRootProperties().filter(n => /^--(gray|grey)-/.test(n));

    expect(neutrals).toEqual([]);
  });

  // ─── Roles ───────────────────────────────────────────────────────────

  /**
   * Each role and the palette entry it must resolve to.
   *
   * 400 declarations were migrated onto these on the strength of one property:
   * a role resolves to the token it replaced, so adopting it cannot change a
   * rendered colour. That is only true while these pairs hold — re-point one and
   * the migration silently stops being a no-op, which is exactly the change
   * somebody would make on purpose and should have to do on purpose.
   */
  const ROLES: ReadonlyArray<readonly [string, string]> = [
    ['--text-heading', '--slate-900'],
    ['--text-strong', '--slate-800'],
    ['--text-body', '--slate-600'],
    ['--text-muted', '--slate-500'],
    ['--text-subtle', '--slate-400'],
    ['--text-inverse', '--white'],
    ['--surface-page', '--slate-50'],
    ['--surface-card', '--white'],
    ['--surface-sunken', '--slate-100'],
    ['--surface-inverse', '--slate-900'],
    ['--border', '--slate-200'],
    ['--border-strong', '--slate-300'],
  ];

  /** Renders `value` as a colour and reads back what the browser resolved. */
  function resolved(value: string): string {
    const probe = document.createElement('div');
    probe.style.color = value;
    document.body.appendChild(probe);

    try {
      return getComputedStyle(probe).color;
    } finally {
      probe.remove();
    }
  }

  ROLES.forEach(([role, palette]) => {
    it(`should resolve ${role} to ${palette}`, () => {
      const resolvedRole = resolved(`var(${role})`);

      // Not just "equal to each other" — a role that resolved to nothing would
      // make both sides the inherited colour and pass.
      expect(resolvedRole).toMatch(/^rgba?\(/);
      expect(resolvedRole).toBe(resolved(`var(${palette})`));
    });
  });

  it('should define every role', () => {
    const declared = declaredRootProperties();

    expect(ROLES.map(([role]) => role).filter(r => !declared.includes(r))).toEqual([]);
  });

  // ─── Channel triples ─────────────────────────────────────────────────

  it('should let the rgb triples drive rgba()', () => {
    // `rgba(var(--brand-rgb), 0.1)` only works because custom properties are
    // substituted before the value is parsed. If that ever stopped holding, the
    // declaration would be dropped as invalid and every tinted surface and
    // shadow in the app would disappear at once.
    const probe = document.createElement('div');
    probe.style.color = 'rgba(var(--brand-rgb), 0.5)';
    document.body.appendChild(probe);

    try {
      expect(getComputedStyle(probe).color).toBe('rgba(63, 81, 181, 0.5)');
    } finally {
      probe.remove();
    }
  });

  // ─── Reach ───────────────────────────────────────────────────────────

  @Component({
    standalone: true,
    // Deliberately a component stylesheet, not a global one: this is the case
    // that has to work. Angular rewrites these selectors to scope them to the
    // component, and the question is whether a :root custom property still
    // reaches the declaration inside.
    template: `<p class="body-copy">text</p>`,
    styles: [`.body-copy { color: var(--slate-600); }`]
  })
  class TokenConsumerComponent {}

  it('should reach a component stylesheet through emulated encapsulation', () => {
    const fixture: ComponentFixture<TokenConsumerComponent> =
      TestBed.createComponent(TokenConsumerComponent);
    fixture.detectChanges();

    const el = fixture.nativeElement.querySelector('.body-copy') as HTMLElement;

    expect(getComputedStyle(el).color).toBe('rgb(71, 85, 105)');
  });
});
