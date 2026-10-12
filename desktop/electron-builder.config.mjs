// Velopack owns installers, signing and updates. This builds its Windows app directory.
export default {
  appId: 'gg.revu.desktop',
  productName: 'Revu',
  executableName: 'revu-desktop',
  // Overwolf requires ASAR integrity and OnlyLoadAppFromAsar on Windows. The
  // archive contains the canonical source modules, without a second UI bundle.
  asar: true,
  // The signed revu-desktop.exe must not double as a general Node runtime or
  // accept a debugger: nothing in electron/ or runtime/ runs the packaged exe
  // with ELECTRON_RUN_AS_NODE, NODE_OPTIONS or --inspect (runtime/overwolf.mjs
  // only runs the unpackaged SDK binary as Node). OW builder forces the two
  // ASAR fuses on for Windows; they are listed so the intent is explicit.
  // scripts/package.mjs verifies the fuse wire of every packaged build.
  electronFuses: {
    runAsNode: false,
    enableNodeOptionsEnvironmentVariable: false,
    enableNodeCliInspectArguments: false,
    enableEmbeddedAsarIntegrityValidation: true,
    onlyLoadAppFromAsar: true,
  },
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
