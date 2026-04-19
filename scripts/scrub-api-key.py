#!/usr/bin/env python3
"""DEPRECATED — use scrub-api-key.ps1 instead.

Python's os.path APIs can't reliably address files in
%LOCALAPPDATA%\\LoLReview\\ because Velopack installs the app as a
packaged app with reparse-point virtualization, and Python sees a
different view of the folder than Windows APIs do. PowerShell's
System.IO.File.ReadAllText respects the real filesystem.
"""
import sys
print(__doc__)
sys.exit(1)
