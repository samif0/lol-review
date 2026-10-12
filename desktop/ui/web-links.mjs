// Links from the desktop app to the Revu website.
//
// My clips lives on revu.lol. The page opens signed out the first time; the
// user signs in on revu.lol with the same email they use here. There is no
// desktop-to-web session handoff in v1.

export const MY_CLIPS_URL = 'https://revu.lol/my-clips';

export async function canOpenExternal(platform) {
  try {
    const caps = await platform.capabilities();
    return caps?.externalLinks === true;
  } catch {
    return false;
  }
}

export async function openMyClips(platform) {
  try {
    await platform.openExternal(MY_CLIPS_URL);
    return true;
  } catch (err) {
    console.warn('[web-links] could not open My clips:', err);
    return false;
  }
}
