"""Build script — creates a standalone LoL Review .exe using PyInstaller.

Usage:
    1. pip install pyinstaller
    2. python build.py

Output lands in dist/LoLReview/LoLReview.exe
Share the entire dist/LoLReview/ folder (or zip it) with friends.
"""

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).parent

def main():
    # Ensure pyinstaller is available
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        print("Installing PyInstaller...")
        subprocess.check_call([sys.executable, "-m", "pip", "install", "pyinstaller"])

    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--name", "LoLReview",
        # No console window (it's a GUI/tray app)
        "--noconsole",
        # Single directory (faster startup than --onefile, easier to debug)
        "--noconfirm",
        # Collect customtkinter's theme files (required or the GUI breaks)
        "--collect-all", "customtkinter",
        # Entry point
        str(ROOT / "run.pyw"),
    ]

    # Add icon if it exists
    icon_path = ROOT / "assets" / "icon.ico"
    if icon_path.exists():
        cmd.extend(["--icon", str(icon_path)])

    print(f"Running: {' '.join(cmd)}\n")
    subprocess.check_call(cmd, cwd=str(ROOT))

    dist_dir = ROOT / "dist" / "LoLReview"
    print(f"\nBuild complete!")
    print(f"  Output: {dist_dir}")
    print(f"  Exe:    {dist_dir / 'LoLReview.exe'}")
    print(f"\nTo share: zip the entire '{dist_dir}' folder and send it.")
    print(f"Each person's data is stored in their own %LOCALAPPDATA%\\LoLReview\\")


if __name__ == "__main__":
    main()
