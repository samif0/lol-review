(() => {
  const lightbox = document.getElementById('lightbox');
  if (!lightbox) return;

  const img = lightbox.querySelector('.lightbox-img');
  const caption = lightbox.querySelector('.lightbox-caption');
  const closeBtn = lightbox.querySelector('.lightbox-close');

  const open = (src, alt, captionText) => {
    img.src = src;
    img.alt = alt || '';
    caption.textContent = captionText || '';
    lightbox.classList.add('is-open');
    lightbox.setAttribute('aria-hidden', 'false');
    document.body.style.overflow = 'hidden';
  };

  const close = () => {
    lightbox.classList.remove('is-open');
    lightbox.setAttribute('aria-hidden', 'true');
    document.body.style.overflow = '';
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
    if (e.key === 'Escape' && lightbox.classList.contains('is-open')) close();
  });
})();
