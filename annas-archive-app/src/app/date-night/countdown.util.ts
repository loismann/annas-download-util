/** Shared by DateNightCountdownComponent (the pre-showtime popup) and
 *  DateNightComponent (the lobby's persistent Locked-state countdown) so the
 *  two don't drift out of sync with their own copies of the same tick/format
 *  logic. */

export function secondsUntil(target: string): number {
  return Math.max(0, Math.round((new Date(target).getTime() - Date.now()) / 1000));
}

/** Schedule slots are stored as Hawaii wall-clock values without an offset.
 * Hawaii is permanently UTC-10 (no DST), so convert explicitly instead of
 * letting `new Date("yyyy-MM-ddTHH:mm")` reinterpret them in the browser's
 * local timezone during an admin dry run. */
export function hawaiiSlotToUtcIso(slot: { date: string; time: string }): string {
  const [year, month, day] = slot.date.split('-').map(Number);
  const [hour, minute] = slot.time.split(':').map(Number);
  return new Date(Date.UTC(year, month - 1, day, hour + 10, minute)).toISOString();
}

export function formatHawaiiSlot(slot: { date: string; time: string }): string {
  return new Date(hawaiiSlotToUtcIso(slot)).toLocaleString(undefined, {
    timeZone: 'Pacific/Honolulu',
    weekday: 'long',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
    timeZoneName: 'short'
  });
}

export function formatCountdown(secondsLeft: number): string {
  if (secondsLeft <= 0) return "Let's go!";
  const h = Math.floor(secondsLeft / 3600);
  const m = Math.floor((secondsLeft % 3600) / 60);
  const s = secondsLeft % 60;
  const pad = (n: number) => n.toString().padStart(2, '0');
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
}
