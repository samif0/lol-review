// Game clocks stay on bookmarks/events; only the media boundary applies origin.
export function timeOrigin(value) {
  return Number.isFinite(value) && value >= -600 && value <= 7200 ? value : 0;
}
export const gameToMedia = (seconds, origin = 0) => Math.max(0, seconds - timeOrigin(origin));
export const mediaToGame = (seconds, origin = 0) => Math.max(0, seconds + timeOrigin(origin));
