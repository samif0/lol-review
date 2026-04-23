(async () => {
  const btn = document.querySelector('#download-btn');
  const versionLabel = document.querySelector('#download-version');
  const footerVersion = document.querySelector('#footer-version');
  if (!btn) return;

  try {
    const res = await fetch(
      'https://api.github.com/repos/samif0/lol-review/releases/latest',
      { headers: { Accept: 'application/vnd.github+json' } }
    );
    if (!res.ok) return;

    const data = await res.json();
    const asset = (data.assets || []).find((a) => /setup\.exe$/i.test(a.name));
    if (asset) {
      btn.href = asset.browser_download_url;
    }
    if (data.tag_name) {
      if (versionLabel) versionLabel.textContent = data.tag_name;
      if (footerVersion) footerVersion.textContent = data.tag_name;
    }
  } catch {
    // Network failed; static href on the button still works.
  }
})();
