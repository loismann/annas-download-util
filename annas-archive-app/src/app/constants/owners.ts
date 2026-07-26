/**
 * The household members, in display order. Single source for every owner
 * picker/filter — previously hardcoded separately in each edit dialog.
 */
export const HOUSEHOLD_OWNERS = ['Paul', 'Mom', 'Dad'] as const;

export type HouseholdOwner = (typeof HOUSEHOLD_OWNERS)[number];
