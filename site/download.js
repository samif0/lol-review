(async () => {
  const btn = document.querySelector('#download-btn');
  const versionLabel = document.querySelector('#download-version');
  const sizeLabel = document.querySelector('#download-size');
  const footerVersion = document.querySelector('#footer-version');
  if (!btn) return;

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

    if (asset) {
      btn.href = asset.browser_download_url;
      if (sizeLabel && typeof asset.size === 'number') {
        const mb = Math.round(asset.size / (1024 * 1024));
        sizeLabel.textContent = ` / ~${mb} MB`;
      }
    }
    if (data.tag_name) {
      if (versionLabel) versionLabel.textContent = data.tag_name;
      if (footerVersion) footerVersion.textContent = data.tag_name;
    }
  } catch {
    // Network failed; static href on the button still works.
  }
})();
