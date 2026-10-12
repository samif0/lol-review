// Display-only wording for the Rules page. User names, descriptions and plans are
// shown as saved; only machine-written records and the banner's plan lead change.
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** "2026-06-10" -> "Jun 10" this year, "Jun 10, 2025" otherwise. */
export function shortDate(iso, now = new Date()) {
  const match = String(iso).match(/^(\d{4})-(\d{2})-(\d{2})$/);
  const month = match ? MONTHS[Number(match[2]) - 1] : null;
  if (!month) return String(iso);
  const day = `${month} ${Number(match[3])}`;
  return Number(match[1]) === now.getFullYear() ? day : `${day}, ${match[1]}`;
}

/** Sentence case for the sidecar's all-caps rule records ("TRIPPED 3× (1W–2L) · …"). */
export function recordLabel(text, now = new Date()) {
  return String(text ?? '')
    .replace(/^NO TRIPS ON RECORD$/, 'No trips on record')
    .replace(/^TRIPPED (?=\d)/, 'Tripped ')
    .replace('BASELINE WR', 'Baseline WR')
    .replace('WR WHEN TRIPPED', 'WR when tripped')
    .replace(' VS ', ' vs ')
    .replace(' BASELINE ·', ' baseline ·')
    .replace(/ · LAST (\d{4}-\d{2}-\d{2})/, (_, iso) => ` · Last ${shortDate(iso, now)}`)
    .replace(/^HELD (?=\d)/, 'Held ')
    .replace(' THIS WEEK', ' this week')
    .replace(' · OVERRIDDEN ', ' · Overridden ')
    // Win-loss records use a plain hyphen: "1W-2L".
    .replace(/(\d+W)\s*[–—]\s*(\d+L)/g, '$1-$2');
}

/** The rule-check banner prints its own "Then" label, so a plan written as
 *  "Then I will..." must not read "Then Then I will...". */
export function planWithoutThen(plan) {
  const text = String(plan ?? '').trim();
  const stripped = text.replace(/^then\b[\s,:]*/i, '');
  return stripped || text;
}
