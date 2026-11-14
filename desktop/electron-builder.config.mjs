// Velopack owns installers, signing and updates. This builds its Windows app directory.
export default {
  appId: 'gg.revu.desktop',
  productName: 'Revu',
  executableName: 'revu-desktop',
  // The app loads only from this archive, and Electron checks its integrity at
  // startup (see the fuses below). It contains the canonical source modules,
  // without a second UI bundle.
  asar: true,
  // The signed revu-desktop.exe must not double as a general Node runtime or
  // accept a debugger: nothing in electron/ or runtime/ runs the packaged exe
  // with ELECTRON_RUN_AS_NODE, NODE_OPTIONS or --inspect (runtime/electron.mjs
  // only runs the unpackaged runtime binary as Node). The builder writes these
  // five fuses, and scripts/package.mjs verifies the fuse wire of every packaged build.
  electronFuses: {
    runAsNode: false,
    enableNodeOptionsEnvironmentVariable: false,
    enableNodeCliInspectArguments: false,
    enableEmbeddedAsarIntegrityValidation: true,
    onlyLoadAppFromAsar: true,
  },
  npmRebuild: false,
  publish: null,
  // The builder's own Windows resource step unpacks a legacy archive containing
  // macOS symlinks. package.mjs uses the pinned rcedit API on the built exe
  // instead, keeping local builds independent of Windows symlink privileges.
  win: { icon: 'electron/branding/revu.ico', target: ['dir'], signAndEditExecutable: false },
  files: ['**/*', '!node_modules/**'],
};
