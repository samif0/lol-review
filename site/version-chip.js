// Keep the header pill's version label fresh, but leave its href pointing
// at /learn (the dedicated desktop-app info page).
(async () => {
  const versionLabel = document.querySelector('#download-chip-version');
  if (!versionLabel) return;
  try {
    const res = await fetch(
      'https://api.github.com/repos/samif0/lol-review/releases/latest',
      { headers: { Accept: 'application/vnd.github+json' } }
    );
    if (!res.ok) return;
    const data = await res.json();
    if (data.tag_name) versionLabel.textContent = data.tag_name;
  } catch {}
})();
