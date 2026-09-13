// Velopack owns installers, signing and updates. This builds its Windows app directory.
export default {
  appId: 'gg.revu.desktop',
  productName: 'Revu',
  executableName: 'revu-desktop',
  // Overwolf requires ASAR integrity and OnlyLoadAppFromAsar on Windows. The
  // archive contains the canonical source modules, without a second UI bundle.
  asar: true,
  npmRebuild: false,
  publish: null,
  // No gaming packages are shipped or activated by the desktop host. A future
  // package-enabled release must add the separate Overwolf signing credentials.
  overwolf: { requireSigning: false },
  // OW builder 26.9.3's Windows resource step unpacks a legacy archive containing
  // macOS symlinks. package.mjs uses Electron's pinned rcedit API on the built exe
  // instead, keeping local builds independent of Windows symlink privileges.
  win: { icon: 'electron/branding/revu.ico', target: ['dir'], signAndEditExecutable: false },
  files: ['**/*', '!node_modules/**'],
};
