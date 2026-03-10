"""Launch LoL Game Review.

Using .pyw extension so Windows runs it without a console window.
Double-click this file to start the app.
"""

import sys
from pathlib import Path

# Ensure the project root is on the path
sys.path.insert(0, str(Path(__file__).parent))

from src.main import main

main()
