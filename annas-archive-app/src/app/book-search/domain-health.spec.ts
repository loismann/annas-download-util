import {
  applyMirrorHealth,
  applySlumHealth,
  getHealthColorClass,
  parseCertExpiry,
  parseHealthPercentage
} from './domain-health';
import { DomainHealth } from '../components/search-form/search-form.component';

/**
 * These moved out of book-search.component.spec.ts when the health logic became
 * plain functions. They are the same assertions, minus a TestBed, a fixture and
 * a component instance none of them ever needed.
 */
describe('domain-health', () => {
  const domains = (): DomainHealth[] => [
    { name: "Anna's Archive GL", extension: 'gl', health: null, certExpDays: null },
    { name: "Anna's Archive PK", extension: 'pk', health: null, certExpDays: null }
  ];

describe('Health status', () => {
  it('should return health-green for health >= 90', () => {
    expect(getHealthColorClass(95)).toBe('health-green');
    expect(getHealthColorClass(90)).toBe('health-green');
  });

  it('should return health-yellow for health >= 70 and < 90', () => {
    expect(getHealthColorClass(85)).toBe('health-yellow');
    expect(getHealthColorClass(70)).toBe('health-yellow');
  });

  it('should return health-orange for health >= 50 and < 70', () => {
    expect(getHealthColorClass(65)).toBe('health-orange');
    expect(getHealthColorClass(50)).toBe('health-orange');
  });

  it('should return health-red for health < 50', () => {
    expect(getHealthColorClass(45)).toBe('health-red');
    expect(getHealthColorClass(0)).toBe('health-red');
  });

  it('should return health-unknown for null health', () => {
    expect(getHealthColorClass(null)).toBe('health-unknown');
  });
});

describe('Boundary tests - Health status exact thresholds', () => {
  it('should return green at exactly 90%', () => {
    expect(getHealthColorClass(90)).toBe('health-green');
  });

  it('should return yellow at exactly 89% (just below green)', () => {
    expect(getHealthColorClass(89)).toBe('health-yellow');
  });

  it('should return yellow at exactly 70%', () => {
    expect(getHealthColorClass(70)).toBe('health-yellow');
  });

  it('should return orange at exactly 69% (just below yellow)', () => {
    expect(getHealthColorClass(69)).toBe('health-orange');
  });

  it('should return orange at exactly 50%', () => {
    expect(getHealthColorClass(50)).toBe('health-orange');
  });

  it('should return red at exactly 49% (just below orange)', () => {
    expect(getHealthColorClass(49)).toBe('health-red');
  });

  it('should return green at exactly 100%', () => {
    expect(getHealthColorClass(100)).toBe('health-green');
  });
});
  describe('parseHealthPercentage', () => {
    it('reads a percentage, whole or fractional', () => {
      expect(parseHealthPercentage('97%')).toBe(97);
      expect(parseHealthPercentage('97.5%')).toBe(97.5);
    });

    it('returns null for anything that is not a percentage', () => {
      expect(parseHealthPercentage('')).toBeNull();
      expect(parseHealthPercentage('unknown')).toBeNull();
      // A bare number is not a percentage — this is what pins the '%' in the
      // pattern. Without it, dropping '%' from the regex passes every test.
      expect(parseHealthPercentage('97')).toBeNull();
    });
  });

  describe('parseCertExpiry', () => {
    it('reads a day count, singular or plural', () => {
      expect(parseCertExpiry('30 days')).toBe(30);
      expect(parseCertExpiry('1 day')).toBe(1);
    });

    it('returns null for anything that is not a day count', () => {
      expect(parseCertExpiry('')).toBeNull();
      expect(parseCertExpiry('soon')).toBeNull();
      // A number in some other unit is not a day count — this is what pins
      // 'days?' in the pattern. Without it, /(\d+)/ passes every test.
      expect(parseCertExpiry('2 hours')).toBeNull();
      expect(parseCertExpiry('12')).toBeNull();
    });
  });

  describe('applyMirrorHealth', () => {
    it('matches on extension and takes the number as given', () => {
      const list = domains();
      applyMirrorHealth(list, [{ extension: 'pk', health: 88 }] as any);

      expect(list[1].health).toBe(88);
      expect(list[0].health).toBeNull();  // untouched
    });

    it('ignores a non-numeric health rather than storing it', () => {
      const list = domains();
      applyMirrorHealth(list, [{ extension: 'gl', health: 'oops' }] as any);

      expect(list[0].health).toBeNull();
    });

    it('survives a null or non-array payload', () => {
      const list = domains();
      expect(() => applyMirrorHealth(list, null as any)).not.toThrow();
      expect(() => applyMirrorHealth(list, {} as any)).not.toThrow();
    });
  });

  describe('applySlumHealth', () => {
    it('matches on display name and parses both strings', () => {
      const list = domains();
      applySlumHealth(list, [
        { name: "Anna's Archive GL", health: '95%', cert_exp: '30 days' }
      ] as any);

      expect(list[0].health).toBe(95);
      expect(list[0].certExpDays).toBe(30);
    });

    it('leaves a domain the monitor does not track alone', () => {
      const list = domains();
      applySlumHealth(list, [
        { name: "Anna's Archive GL", health: '95%', cert_exp: '30 days' }
      ] as any);

      expect(list[1].health).toBeNull();
      expect(list[1].certExpDays).toBeNull();
    });

    it('survives a null or non-array payload', () => {
      const list = domains();
      expect(() => applySlumHealth(list, null as any)).not.toThrow();
      expect(() => applySlumHealth(list, {} as any)).not.toThrow();
    });
  });
});
