(() => {
  const lightbox = document.getElementById('lightbox');
  if (!lightbox) return;

  const img = lightbox.querySelector('.lightbox-img');
  const caption = lightbox.querySelector('.lightbox-caption');
  const closeBtn = lightbox.querySelector('.lightbox-close');

  // The element that opened the lightbox gets focus back when it closes.
  let opener = null;

  const open = (src, alt, captionText) => {
    opener = document.activeElement;
    img.src = src;
    img.alt = alt || '';
    caption.textContent = captionText || '';
    lightbox.classList.add('is-open');
    lightbox.setAttribute('aria-hidden', 'false');
    lightbox.setAttribute('aria-modal', 'true');
    document.body.style.overflow = 'hidden';
    if (closeBtn) closeBtn.focus();
  };

  const close = () => {
    lightbox.classList.remove('is-open');
    lightbox.setAttribute('aria-hidden', 'true');
    lightbox.removeAttribute('aria-modal');
    document.body.style.overflow = '';
    if (opener && typeof opener.focus === 'function') opener.focus();
    opener = null;
    // clear src after transition so a stale flash doesn't appear next open
    setTimeout(() => {
      if (!lightbox.classList.contains('is-open')) img.src = '';
    }, 200);
  };

  document.querySelectorAll('.screenshot-hero, .screenshot-tile').forEach((fig) => {
    const image = fig.querySelector('img');
    const cap = fig.querySelector('figcaption');
    if (!image) return;
    fig.style.cursor = 'zoom-in';
    fig.setAttribute('role', 'button');
    fig.setAttribute('tabindex', '0');
    fig.setAttribute('aria-label', `Expand ${cap ? cap.textContent.trim() : 'screenshot'}`);
    const trigger = (e) => {
      e.preventDefault();
      open(image.currentSrc || image.src, image.alt, cap ? cap.textContent : '');
    };
    fig.addEventListener('click', trigger);
    fig.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') trigger(e);
    });
  });

  lightbox.addEventListener('click', (e) => {
    if (e.target === lightbox || e.target === closeBtn) close();
  });
  document.addEventListener('keydown', (e) => {
    if (!lightbox.classList.contains('is-open')) return;
    if (e.key === 'Escape') close();
    // The close button is the only control inside, so Tab stays on it.
    if (e.key === 'Tab' && closeBtn) {
      e.preventDefault();
      closeBtn.focus();
    }
  });
})();
