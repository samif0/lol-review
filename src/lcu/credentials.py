"""LCU credential discovery — finds the running League client's auth info."""

import logging
import os
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)


@dataclass
class LCUCredentials:
    """Auth credentials parsed from the League client lockfile or process."""

    pid: int
    port: int
    password: str
    protocol: str = "https"

    @property
    def base_url(self) -> str:
        return f"{self.protocol}://127.0.0.1:{self.port}"

    @property
    def auth(self) -> tuple[str, str]:
        return ("riot", self.password)

    @property
    def ws_url(self) -> str:
        return f"wss://riot:{self.password}@127.0.0.1:{self.port}/"


def find_credentials_from_process() -> Optional[LCUCredentials]:
    """Find LCU credentials by inspecting the LeagueClientUx process args.

    This is the most reliable method — works regardless of install location.
    """
    try:
        result = subprocess.run(
            ["wmic", "PROCESS", "WHERE", "name='LeagueClientUx.exe'",
             "GET", "commandline"],
            capture_output=True, text=True, timeout=5,
        )
        output = result.stdout

        if not output or "LeagueClientUx" not in output:
            return None

        port_match = re.search(r"--app-port=(\d+)", output)
        token_match = re.search(r"--remoting-auth-token=([\w_-]+)", output)
        pid_match = re.search(r"--app-pid=(\d+)", output)

        if port_match and token_match:
            return LCUCredentials(
                pid=int(pid_match.group(1)) if pid_match else 0,
                port=int(port_match.group(1)),
                password=token_match.group(1),
            )
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.debug(f"Process inspection failed: {e}")

    return None


def find_credentials_from_lockfile() -> Optional[LCUCredentials]:
    """Find LCU credentials by reading the lockfile.

    The lockfile is created when the client starts and removed when it stops.
    Common locations are checked in order.
    """
    common_paths = [
        Path(os.environ.get("LEAGUE_PATH", "")) / "lockfile",
        Path("C:/Riot Games/League of Legends/lockfile"),
        Path("D:/Riot Games/League of Legends/lockfile"),
        Path(os.path.expanduser("~")) / "Riot Games" / "League of Legends" / "lockfile",
    ]

    for lockfile_path in common_paths:
        if lockfile_path.exists():
            try:
                content = lockfile_path.read_text().strip()
                parts = content.split(":")
                if len(parts) >= 5:
                    return LCUCredentials(
                        pid=int(parts[1]),
                        port=int(parts[2]),
                        password=parts[3],
                        protocol=parts[4],
                    )
            except (ValueError, IOError) as e:
                logger.debug(f"Failed to read lockfile at {lockfile_path}: {e}")

    return None


def find_credentials() -> Optional[LCUCredentials]:
    """Find LCU credentials using all available methods."""
    creds = find_credentials_from_process()
    if creds:
        logger.info(f"Found LCU via process inspection (port {creds.port})")
        return creds

    creds = find_credentials_from_lockfile()
    if creds:
        logger.info(f"Found LCU via lockfile (port {creds.port})")
        return creds

    return None
