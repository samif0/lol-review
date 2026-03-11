"""Build script — creates a standalone LoL Review .exe using PyInstaller.

Usage:
    1. pip install -r requirements.txt
    2. python scripts/download_mpv.py   (downloads libmpv-2.dll into deps/)
    3. python build.py

Output lands in dist/LoLReview/LoLReview.exe
Share the entire dist/LoLReview/ folder (or zip it) with friends.
End users don't need to install anything — it's all bundled.
"""

import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).parent
DEPS_DIR = ROOT / "deps"
DIST_DIR = ROOT / "dist" / "LoLReview"
MPV_DLL = DEPS_DIR / "libmpv-2.dll"


def main():
    # Ensure pyinstaller is available
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        print("Installing PyInstaller...")
        subprocess.check_call([sys.executable, "-m", "pip", "install", "pyinstaller"])

    # Check for libmpv-2.dll
    if not MPV_DLL.exists():
        print("=" * 60)
        print("  WARNING: libmpv-2.dll not found in deps/")
        print("=" * 60)
        print()
        print("Run this first to download it:")
        print("    python scripts/download_mpv.py")
        print()
        print("Without it, the app will still work but VOD playback")
        print("will open videos in the system default player instead")
        print("of the embedded player.")
        print()
        resp = input("Continue building without embedded VOD? [y/N]: ").strip().lower()
        if resp != "y":
            return

    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--name", "LoLReview",
        # No console window (it's a GUI/tray app)
        "--noconsole",
        # Single directory (faster startup than --onefile, easier to debug)
        "--noconfirm",
        # Collect customtkinter's theme files (required or the GUI breaks)
        "--collect-all", "customtkinter",
        # Hidden imports for mpv
        "--hidden-import", "mpv",
    ]

    # Bundle libmpv-2.dll if present
    if MPV_DLL.exists():
        cmd.extend(["--add-binary", f"{MPV_DLL}{os.pathsep}."])
        print(f"Bundling libmpv-2.dll ({MPV_DLL.stat().st_size / (1024*1024):.1f} MB)")

    # Add icon if it exists
    icon_path = ROOT / "assets" / "icon.ico"
    if icon_path.exists():
        cmd.extend(["--icon", str(icon_path)])

    # Entry point
    cmd.append(str(ROOT / "run.pyw"))

    print(f"\nRunning: {' '.join(cmd)}\n")
    subprocess.check_call(cmd, cwd=str(ROOT))

    print(f"\nBuild complete!")
    print(f"  Output: {DIST_DIR}")
    print(f"  Exe:    {DIST_DIR / 'LoLReview.exe'}")
    if MPV_DLL.exists():
        print(f"  VOD:    Embedded mpv player bundled ✓")
    else:
        print(f"  VOD:    External player fallback (no libmpv)")
    print(f"\nTo share: zip the entire '{DIST_DIR}' folder and send it.")
    print(f"Each person's data is stored in their own %LOCALAPPDATA%\\LoLReview\\")


if __name__ == "__main__":
    main()
