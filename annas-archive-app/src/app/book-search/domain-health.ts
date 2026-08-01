import { DomainHealth } from '../components/search-form/search-form.component';
import {
  SlumHealthEntry,
  SlumHealthResponse,
  MirrorHealthEntry,
  MirrorHealthResponse
} from '../models/health-check.model';

/**
 * Reading the two Anna's Archive health feeds into the domain chips.
 *
 * Pure functions, no injectable: they take the domain list and mutate it in
 * place exactly as the component's private methods did. Split out of
 * BookSearchComponent because the string parsing and the colour thresholds are
 * worth testing directly, and because `getHealthColorClass` existed twice —
 * byte-identical in BookSearchComponent and SearchFormComponent, with only the
 * latter's copy actually reachable from a template.
 */

/** "97.5%" -> 97.5. Null for anything that isn't a percentage. */
export function parseHealthPercentage(healthString: string): number | null {
  if (!healthString) return null;
  const match = healthString.match(/(\d+\.?\d*)%/);
  return match ? parseFloat(match[1]) : null;
}

/** "30 days" -> 30. Null for anything that isn't a day count. */
export function parseCertExpiry(certExp: string): number | null {
  if (!certExp) return null;
  const match = certExp.match(/(\d+)\s*days?/);
  return match ? parseInt(match[1], 10) : null;
}

export function getHealthColorClass(health: number | null): string {
  if (health === null) return 'health-unknown';
  if (health >= 90) return 'health-green';
  if (health >= 70) return 'health-yellow';
  if (health >= 50) return 'health-orange';
  return 'health-red';
}

/**
 * Our own backend, hitting the domains directly — the reliable source. Health
 * arrives already numeric here, so there is nothing to parse.
 */
export function applyMirrorHealth(domains: DomainHealth[], data: MirrorHealthResponse): void {
  if (!data || !Array.isArray(data)) return;

  data.forEach((entry: MirrorHealthEntry) => {
    if (!entry?.extension) return;
    const domain = domains.find(d => d.extension === entry.extension);
    if (!domain) return;

    domain.health = typeof entry.health === 'number' ? entry.health : null;
  });
}

/**
 * Best-effort: this data comes from a third-party status monitor
 * (open-slum.org) we don't control, so these entries only populate if that
 * service happens to track the current domains. applyMirrorHealth is the
 * reliable source — this is a bonus enrichment layered on top when available.
 *
 * Matching is by the chip's own display name, so adding a fourth mirror to
 * `annaDomains` is enough; this needs no edit (the previous version named
 * "gl"/"pk"/"gd" three times over).
 */
export function applySlumHealth(domains: DomainHealth[], data: SlumHealthResponse): void {
  if (!data || !Array.isArray(data)) return;

  domains.forEach(domain => {
    const entry = data.find((e: SlumHealthEntry) => e.name === domain.name);
    if (!entry) return;

    domain.health = parseHealthPercentage(entry.health);
    domain.certExpDays = parseCertExpiry(entry.cert_exp);
  });
}
