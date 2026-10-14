// Download links on the home page. Every a[data-download] keeps its static href
// (the latest release's Revu-Setup.exe) as the no-JS and offline fallback; this
// swaps in the exact asset URL and fills the version and size labels. Text is
// only ever written with textContent.
(async () => {
  const links = [...document.querySelectorAll('a[data-download]')];
  if (!links.length) return;

  // Phones and Macs can't run the installer, so say so instead of letting a
  // Windows .exe land on them. The buttons stay as they are.
  const nav = typeof navigator === 'undefined' ? {} : navigator;
  const platform = (nav.userAgentData && nav.userAgentData.platform) || nav.userAgent || '';
  if (platform && !/windows/i.test(platform)) {
    for (const note of document.querySelectorAll('[data-download-note]')) {
      note.textContent = 'Revu is a Windows app. Open this page on your PC to download it, or review a game in your browser.';
      note.hidden = false;
    }
  }

  try {
    const res = await fetch(
      'https://api.github.com/repos/samif0/lol-review/releases/latest',
      { headers: { Accept: 'application/vnd.github+json' } }
    );
    if (!res.ok) return;

    const data = await res.json();
    const assets = data.assets || [];
    // Prefer the cleanly-named installer (Revu-Setup.exe). Fall back to the
    // Velopack default (LoLReview-win-Setup.exe) for older releases that
    // predate the renamed copy.
    const asset =
      assets.find((a) => /^revu.*setup\.exe$/i.test(a.name)) ||
      assets.find((a) => /setup\.exe$/i.test(a.name));

    if (asset && /^https:\/\/github\.com\//.test(asset.browser_download_url || '')) {
      for (const link of links) link.href = asset.browser_download_url;
      if (typeof asset.size === 'number' && asset.size > 0) {
        const mb = Math.max(1, Math.round(asset.size / (1024 * 1024)));
        for (const label of document.querySelectorAll('[data-download-size]')) label.textContent = ` · ~${mb} MB`;
      }
    }
    if (typeof data.tag_name === 'string' && data.tag_name) {
      for (const label of document.querySelectorAll('[data-download-version]')) label.textContent = data.tag_name;
      const footerVersion = document.querySelector('#footer-version');
      if (footerVersion) footerVersion.textContent = data.tag_name;
    }
  } catch {
    // Network or API failure: the static installer links still work.
  }
})();
