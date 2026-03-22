"""Tests for the auto-updater system.

Covers: version parsing, pointer files, SxS installation, update detection,
download/install flow, cleanup, and startup safety logic.
"""

import json
import os
import shutil
import tempfile
import zipfile
from pathlib import Path
from unittest.mock import MagicMock, patch, PropertyMock

import pytest

# We need to be able to import updater functions without the full app context.
# Patch out the imports that require config/constants before importing.
import sys

# Create a minimal src package structure for import
sys.path.insert(0, str(Path(__file__).parent.parent))


# ── Version parsing ────────────────────────────────────────────────

from src.updater import parse_version


class TestParseVersion:
    def test_simple_version(self):
        assert parse_version("1.5.5") == (1, 5, 5)

    def test_v_prefix(self):
        assert parse_version("v1.5.5") == (1, 5, 5)

    def test_V_prefix(self):
        assert parse_version("V1.5.5") == (1, 5, 5)

    def test_two_part_version(self):
        assert parse_version("1.5") == (1, 5)

    def test_four_part_version(self):
        assert parse_version("1.5.5.1") == (1, 5, 5, 1)

    def test_trailing_whitespace(self):
        assert parse_version("v1.5.5  ") == (1, 5, 5)

    def test_leading_whitespace_edge_case(self):
        # Leading spaces before 'v' prevent lstrip("vV") from stripping it
        # This is expected — GitHub tags never have leading spaces
        assert parse_version("  v1.5.5") == (0, 0, 0)

    def test_invalid_returns_zero(self):
        assert parse_version("invalid") == (0, 0, 0)

    def test_empty_returns_zero(self):
        assert parse_version("") == (0, 0, 0)

    def test_comparison_newer(self):
        assert parse_version("1.5.6") > parse_version("1.5.5")

    def test_comparison_older(self):
        assert parse_version("1.5.4") < parse_version("1.5.5")

    def test_comparison_equal(self):
        assert parse_version("1.5.5") == parse_version("v1.5.5")

    def test_comparison_major(self):
        assert parse_version("2.0.0") > parse_version("1.99.99")

    def test_comparison_two_vs_three_parts(self):
        # Python tuple comparison: (1, 5) < (1, 5, 0) is True
        assert parse_version("1.5") < parse_version("1.5.0")

    def test_comparison_two_vs_three_same_prefix(self):
        assert parse_version("1.5") < parse_version("1.5.1")


# ── Pointer files ──────────────────────────────────────────────────

from src.updater import _read_pointer, _write_pointer_atomic


class TestPointerFiles:
    def test_read_existing(self, tmp_path):
        p = tmp_path / ".current"
        p.write_text("1.5.5", encoding="utf-8")
        assert _read_pointer(p) == "1.5.5"

    def test_read_with_whitespace(self, tmp_path):
        p = tmp_path / ".current"
        p.write_text("  1.5.5  \n", encoding="utf-8")
        assert _read_pointer(p) == "1.5.5"

    def test_read_missing_returns_empty(self, tmp_path):
        p = tmp_path / ".current"
        assert _read_pointer(p) == ""

    def test_write_atomic(self, tmp_path):
        p = tmp_path / ".current"
        _write_pointer_atomic(p, "1.5.6")
        assert p.read_text(encoding="utf-8") == "1.5.6"

    def test_write_atomic_overwrites(self, tmp_path):
        p = tmp_path / ".current"
        p.write_text("1.5.5", encoding="utf-8")
        _write_pointer_atomic(p, "1.5.6")
        assert p.read_text(encoding="utf-8") == "1.5.6"

    def test_write_atomic_no_tmp_leftover(self, tmp_path):
        p = tmp_path / ".current"
        _write_pointer_atomic(p, "1.5.6")
        assert not (tmp_path / ".current.tmp").exists()


# ── SxS installation ──────────────────────────────────────────────

from src.updater import _install_sxs, _find_app_source


class TestFindAppSource:
    def test_finds_versioned_dir(self, tmp_path):
        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        result = _find_app_source(tmp_path, "1.5.6")
        assert result == app_dir

    def test_finds_any_app_dir(self, tmp_path):
        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        # Search for different version — should fall back to any app-* dir
        result = _find_app_source(tmp_path, "1.5.7")
        assert result == app_dir

    def test_single_subfolder_fallback(self, tmp_path):
        sub = tmp_path / "LoLReview"
        sub.mkdir()
        (sub / "LoLReview.exe").touch()
        result = _find_app_source(tmp_path, "1.5.6")
        assert result == sub

    def test_root_fallback(self, tmp_path):
        (tmp_path / "LoLReview.exe").touch()
        (tmp_path / "other.dll").touch()
        result = _find_app_source(tmp_path, "1.5.6")
        assert result == tmp_path


class TestInstallSxs:
    def test_creates_app_dir(self, tmp_path):
        source = tmp_path / "source"
        source.mkdir()
        (source / "LoLReview.exe").touch()
        (source / "data.dll").touch()

        root = tmp_path / "root"
        root.mkdir()

        _install_sxs(root, source, "1.5.6")

        assert (root / "app-1.5.6").is_dir()
        assert (root / "app-1.5.6" / "LoLReview.exe").exists()
        assert (root / "app-1.5.6" / "data.dll").exists()

    def test_updates_current_pointer(self, tmp_path):
        source = tmp_path / "source"
        source.mkdir()
        (source / "LoLReview.exe").touch()

        root = tmp_path / "root"
        root.mkdir()

        _install_sxs(root, source, "1.5.6")

        assert (root / ".current").read_text(encoding="utf-8") == "1.5.6"

    def test_saves_previous_version(self, tmp_path):
        source = tmp_path / "source"
        source.mkdir()
        (source / "LoLReview.exe").touch()

        root = tmp_path / "root"
        root.mkdir()
        (root / ".current").write_text("1.5.5", encoding="utf-8")

        _install_sxs(root, source, "1.5.6")

        assert (root / ".current").read_text(encoding="utf-8") == "1.5.6"
        assert (root / ".previous").read_text(encoding="utf-8") == "1.5.5"

    def test_writes_update_pending(self, tmp_path):
        source = tmp_path / "source"
        source.mkdir()
        (source / "LoLReview.exe").touch()

        root = tmp_path / "root"
        root.mkdir()

        _install_sxs(root, source, "1.5.6")

        assert (root / ".update_pending").read_text(encoding="utf-8") == "1.5.6"

    def test_replaces_existing_version_dir(self, tmp_path):
        source = tmp_path / "source"
        source.mkdir()
        (source / "LoLReview.exe").touch()
        (source / "new_file.txt").touch()

        root = tmp_path / "root"
        root.mkdir()
        old_app = root / "app-1.5.6"
        old_app.mkdir()
        (old_app / "LoLReview.exe").touch()
        (old_app / "old_file.txt").touch()

        _install_sxs(root, source, "1.5.6")

        assert (root / "app-1.5.6" / "new_file.txt").exists()
        assert not (root / "app-1.5.6" / "old_file.txt").exists()

    def test_raises_on_no_exe(self, tmp_path):
        source = tmp_path / "source"
        source.mkdir()
        (source / "readme.txt").touch()  # No exe

        root = tmp_path / "root"
        root.mkdir()

        with pytest.raises(RuntimeError, match="No exe"):
            _install_sxs(root, source, "1.5.6")


# ── Version cleanup ────────────────────────────────────────────────

from src.updater import cleanup_old_versions


class TestCleanupOldVersions:
    @patch("src.updater.get_sxs_root")
    def test_removes_old_versions(self, mock_root, tmp_path):
        mock_root.return_value = tmp_path

        # Current and previous should be kept
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")
        (tmp_path / ".previous").write_text("1.5.5", encoding="utf-8")

        (tmp_path / "app-1.5.4").mkdir()
        (tmp_path / "app-1.5.5").mkdir()
        (tmp_path / "app-1.5.6").mkdir()

        cleanup_old_versions()

        assert not (tmp_path / "app-1.5.4").exists()
        assert (tmp_path / "app-1.5.5").exists()
        assert (tmp_path / "app-1.5.6").exists()

    @patch("src.updater.get_sxs_root")
    def test_keeps_non_app_dirs(self, mock_root, tmp_path):
        mock_root.return_value = tmp_path

        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")
        (tmp_path / ".previous").write_text("1.5.5", encoding="utf-8")

        (tmp_path / "config").mkdir()
        (tmp_path / "logs").mkdir()
        (tmp_path / "app-1.5.6").mkdir()

        cleanup_old_versions()

        assert (tmp_path / "config").exists()
        assert (tmp_path / "logs").exists()

    @patch("src.updater.get_sxs_root")
    def test_noop_when_not_sxs(self, mock_root, tmp_path):
        mock_root.return_value = None
        # Should not raise
        cleanup_old_versions()


# ── Update check ───────────────────────────────────────────────────

from src.updater import check_for_update


class TestCheckForUpdate:
    @patch("src.updater.requests.get")
    @patch("src.updater._get_install_root")
    @patch("src.updater.__version__", "1.5.5")
    def test_detects_newer_version(self, mock_root, mock_get, tmp_path):
        mock_root.return_value = tmp_path

        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.json.return_value = {
            "tag_name": "v1.5.6",
            "html_url": "https://github.com/samif0/lol-review/releases/tag/v1.5.6",
            "body": "Bug fixes",
            "assets": [
                {"name": "LoLReview-v1.5.6.zip", "browser_download_url": "https://example.com/dl.zip"}
            ],
        }
        mock_get.return_value = mock_resp

        result = check_for_update()
        assert result is not None
        assert result["clean_version"] == "1.5.6"
        assert result["download_url"] == "https://example.com/dl.zip"
        assert result["already_installed"] is False

    @patch("src.updater.requests.get")
    @patch("src.updater.__version__", "1.5.5")
    def test_no_update_when_current(self, mock_get):
        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.json.return_value = {
            "tag_name": "v1.5.5",
            "assets": [],
        }
        mock_get.return_value = mock_resp

        result = check_for_update()
        assert result is None

    @patch("src.updater.requests.get")
    @patch("src.updater.__version__", "1.5.5")
    def test_no_update_when_older_remote(self, mock_get):
        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.json.return_value = {
            "tag_name": "v1.5.4",
            "assets": [],
        }
        mock_get.return_value = mock_resp

        result = check_for_update()
        assert result is None

    @patch("src.updater.requests.get")
    @patch("src.updater.__version__", "1.5.5")
    def test_handles_404(self, mock_get):
        mock_resp = MagicMock()
        mock_resp.status_code = 404
        mock_get.return_value = mock_resp

        result = check_for_update()
        assert result is None

    @patch("src.updater.requests.get")
    @patch("src.updater.__version__", "1.5.5")
    def test_handles_network_error(self, mock_get):
        mock_get.side_effect = Exception("Connection refused")

        result = check_for_update()
        assert result is None

    @patch("src.updater.requests.get")
    @patch("src.updater._get_install_root")
    @patch("src.updater.__version__", "1.5.5")
    def test_detects_already_installed(self, mock_root, mock_get, tmp_path):
        mock_root.return_value = tmp_path

        # Simulate already-installed version
        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()

        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.json.return_value = {
            "tag_name": "v1.5.6",
            "assets": [{"name": "LoLReview-v1.5.6.zip", "browser_download_url": "https://example.com/dl.zip"}],
        }
        mock_get.return_value = mock_resp

        result = check_for_update()
        assert result is not None
        assert result["already_installed"] is True


# ── Download & install flow ────────────────────────────────────────

from src.updater import _do_download_and_install


class TestDownloadAndInstall:
    @patch("src.updater._get_install_root")
    @patch("src.updater.requests.get")
    def test_full_download_install_flow(self, mock_get, mock_root, tmp_path):
        install_root = tmp_path / "install"
        install_root.mkdir()
        mock_root.return_value = install_root

        # Create a ZIP with app-1.5.6/LoLReview.exe
        zip_path = tmp_path / "update.zip"
        with zipfile.ZipFile(zip_path, "w") as zf:
            zf.writestr("app-1.5.6/LoLReview.exe", "fake exe content")
            zf.writestr("app-1.5.6/_internal/data.bin", "data")

        zip_data = zip_path.read_bytes()

        # Mock the download response
        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.headers = {"content-length": str(len(zip_data))}
        mock_resp.iter_content = MagicMock(return_value=[zip_data])
        mock_resp.raise_for_status = MagicMock()
        mock_get.return_value = mock_resp

        progress_calls = []
        _do_download_and_install(
            "https://example.com/dl.zip",
            target_version="v1.5.6",
            on_progress=lambda d, t: progress_calls.append((d, t)),
        )

        # Verify installation
        assert (install_root / "app-1.5.6" / "LoLReview.exe").exists()
        assert (install_root / "app-1.5.6" / "_internal" / "data.bin").exists()
        assert (install_root / ".current").read_text(encoding="utf-8") == "1.5.6"
        assert (install_root / ".update_pending").exists()
        assert len(progress_calls) > 0

    @patch("src.updater._get_install_root")
    @patch("src.updater.requests.get")
    def test_raises_on_no_exe_in_zip(self, mock_get, mock_root, tmp_path):
        install_root = tmp_path / "install"
        install_root.mkdir()
        mock_root.return_value = install_root

        # ZIP without any exe
        zip_path = tmp_path / "bad.zip"
        with zipfile.ZipFile(zip_path, "w") as zf:
            zf.writestr("app-1.5.6/readme.txt", "no exe here")

        zip_data = zip_path.read_bytes()
        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.headers = {"content-length": str(len(zip_data))}
        mock_resp.iter_content = MagicMock(return_value=[zip_data])
        mock_resp.raise_for_status = MagicMock()
        mock_get.return_value = mock_resp

        with pytest.raises(RuntimeError, match="No .exe"):
            _do_download_and_install("https://example.com/dl.zip", "v1.5.6")


# ── Post-update startup ───────────────────────────────────────────

from src.updater import post_update_startup


class TestPostUpdateStartup:
    @patch("src.updater.get_sxs_root")
    @patch("src.updater.__version__", "1.5.6")
    def test_detects_pending_update(self, mock_root, tmp_path):
        mock_root.return_value = tmp_path
        (tmp_path / ".update_pending").write_text("1.5.6", encoding="utf-8")

        result = post_update_startup()
        assert result is True
        assert not (tmp_path / ".update_pending").exists()  # Lock cleaned up

    @patch("src.updater.get_sxs_root")
    def test_no_pending(self, mock_root, tmp_path):
        mock_root.return_value = tmp_path

        result = post_update_startup()
        assert result is False

    @patch("src.updater.get_sxs_root")
    @patch("src.updater.__version__", "1.5.5")
    def test_version_mismatch_still_returns_true(self, mock_root, tmp_path):
        mock_root.return_value = tmp_path
        (tmp_path / ".update_pending").write_text("1.5.6", encoding="utf-8")

        result = post_update_startup()
        assert result is True  # Still considers it a post-update


# ── Restart batch generation ───────────────────────────────────────

from src.updater import restart_into_version


class TestRestartIntoVersion:
    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    def test_generates_batch_file(self, mock_root, mock_popen, tmp_path):
        mock_root.return_value = tmp_path

        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        result = restart_into_version("1.5.6")

        assert result is True
        assert mock_popen.called

        # Check the batch file was created
        cmd_path = tmp_path / "_restart.cmd"
        assert cmd_path.exists()
        content = cmd_path.read_text(encoding="mbcs")
        assert "tasklist" in content
        assert "LoLReview.exe" in content
        assert str(os.getpid()) in content

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    def test_batch_uses_findstr_for_pid_detection(self, mock_root, mock_popen, tmp_path):
        """Batch script should use 'findstr /B /C:\"INFO:\"' for reliable PID detection."""
        mock_root.return_value = tmp_path

        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        restart_into_version("1.5.6")

        content = (tmp_path / "_restart.cmd").read_text(encoding="mbcs")
        # Should use findstr /B /C:"INFO:" instead of find "{pid}"
        assert 'findstr /B /C:"INFO:"' in content
        # Should use /NH flag on tasklist (no header)
        assert "/NH" in content

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    def test_batch_has_timeout(self, mock_root, mock_popen, tmp_path):
        """Batch script should have a retry counter so it can't loop forever."""
        mock_root.return_value = tmp_path

        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        restart_into_version("1.5.6")

        content = (tmp_path / "_restart.cmd").read_text(encoding="mbcs")
        assert "retries" in content
        assert "GEQ 60" in content  # 60 iterations ~= 60 seconds timeout

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    def test_batch_has_launch_label(self, mock_root, mock_popen, tmp_path):
        """Batch script should jump to :launch when PID is gone or timeout."""
        mock_root.return_value = tmp_path

        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        restart_into_version("1.5.6")

        content = (tmp_path / "_restart.cmd").read_text(encoding="mbcs")
        assert ":launch" in content
        assert "goto launch" in content

    @patch("src.updater._get_install_root")
    def test_fails_when_exe_missing(self, mock_root, tmp_path):
        mock_root.return_value = tmp_path

        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        # No LoLReview.exe
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        result = restart_into_version("1.5.6")
        assert result is False

    @patch("src.updater._get_install_root")
    def test_fails_when_no_version(self, mock_root, tmp_path):
        mock_root.return_value = tmp_path
        # No .current file

        result = restart_into_version("")
        assert result is False

    @patch("src.updater._get_install_root")
    def test_fails_when_no_install_root(self, mock_root):
        mock_root.return_value = None

        result = restart_into_version("1.5.6")
        assert result is False


# ── set_current_version ────────────────────────────────────────────

from src.updater import set_current_version


class TestSetCurrentVersion:
    @patch("src.updater._get_install_root")
    def test_sets_current_and_previous(self, mock_root, tmp_path):
        mock_root.return_value = tmp_path
        (tmp_path / ".current").write_text("1.5.5", encoding="utf-8")

        set_current_version("1.5.6")

        assert (tmp_path / ".current").read_text(encoding="utf-8") == "1.5.6"
        assert (tmp_path / ".previous").read_text(encoding="utf-8") == "1.5.5"
        assert (tmp_path / ".update_pending").read_text(encoding="utf-8") == "1.5.6"

    @patch("src.updater._get_install_root")
    def test_noop_when_same_version(self, mock_root, tmp_path):
        mock_root.return_value = tmp_path
        (tmp_path / ".current").write_text("1.5.5", encoding="utf-8")

        set_current_version("1.5.5")

        # Should still write update_pending
        assert (tmp_path / ".current").read_text(encoding="utf-8") == "1.5.5"
        assert not (tmp_path / ".previous").exists()  # No previous since version didn't change


# ── End-to-end update flow ────────────────────────────────────────


class TestEndToEndUpdateFlow:
    """Simulates the full update lifecycle:
    check → download → extract → install → batch script → post-update startup.
    """

    def _make_update_zip(self, tmp_path, version="1.5.6"):
        """Create a fake update ZIP with an app directory and exe."""
        zip_path = tmp_path / f"LoLReview-v{version}.zip"
        with zipfile.ZipFile(zip_path, "w") as zf:
            zf.writestr(f"app-{version}/LoLReview.exe", "fake exe content")
            zf.writestr(f"app-{version}/_internal/data.bin", "runtime data")
            zf.writestr(f"app-{version}/_internal/base_library.zip", "stdlib")
        return zip_path

    def _make_install_root(self, tmp_path, current_version="1.5.5"):
        """Create a fake SxS install root with a current version."""
        root = tmp_path / "install"
        root.mkdir()
        app_dir = root / f"app-{current_version}"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (root / ".current").write_text(current_version, encoding="utf-8")
        return root

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    @patch("src.updater.requests.get")
    @patch("src.updater.__version__", "1.5.5")
    def test_full_flow_check_download_install_restart(
        self, mock_get, mock_root, mock_popen, tmp_path
    ):
        """End-to-end: detect update → download → install → batch script → post-update."""
        install_root = self._make_install_root(tmp_path)
        mock_root.return_value = install_root

        # ── Step 1: check_for_update detects newer version ──
        mock_resp_check = MagicMock()
        mock_resp_check.status_code = 200
        mock_resp_check.json.return_value = {
            "tag_name": "v1.5.6",
            "html_url": "https://github.com/samif0/lol-review/releases/tag/v1.5.6",
            "body": "Bug fixes and improvements",
            "assets": [
                {
                    "name": "LoLReview-v1.5.6.zip",
                    "browser_download_url": "https://example.com/LoLReview-v1.5.6.zip",
                }
            ],
        }
        mock_get.return_value = mock_resp_check

        update_info = check_for_update()
        assert update_info is not None
        assert update_info["clean_version"] == "1.5.6"
        assert update_info["already_installed"] is False

        # ── Step 2: download and install ──
        zip_path = self._make_update_zip(tmp_path)
        zip_data = zip_path.read_bytes()

        mock_resp_dl = MagicMock()
        mock_resp_dl.status_code = 200
        mock_resp_dl.headers = {"content-length": str(len(zip_data))}
        mock_resp_dl.iter_content = MagicMock(return_value=[zip_data])
        mock_resp_dl.raise_for_status = MagicMock()
        mock_get.return_value = mock_resp_dl

        progress_calls = []
        _do_download_and_install(
            "https://example.com/LoLReview-v1.5.6.zip",
            target_version="v1.5.6",
            on_progress=lambda d, t: progress_calls.append((d, t)),
        )

        # Verify SxS installation
        assert (install_root / "app-1.5.6").is_dir()
        assert (install_root / "app-1.5.6" / "LoLReview.exe").exists()
        assert (install_root / "app-1.5.6" / "_internal" / "data.bin").exists()
        assert (install_root / ".current").read_text(encoding="utf-8") == "1.5.6"
        assert (install_root / ".previous").read_text(encoding="utf-8") == "1.5.5"
        assert (install_root / ".update_pending").exists()
        assert len(progress_calls) > 0

        # Old version directory should still exist (cleanup happens later)
        assert (install_root / "app-1.5.5").is_dir()

        # ── Step 3: restart_into_version generates correct batch ──
        result = restart_into_version("1.5.6")
        assert result is True
        assert mock_popen.called

        cmd_path = install_root / "_restart.cmd"
        assert cmd_path.exists()
        batch_content = cmd_path.read_text(encoding="mbcs")

        # Verify batch script correctness
        pid = str(os.getpid())
        assert pid in batch_content
        assert 'findstr /B /C:"INFO:"' in batch_content
        assert "/NH" in batch_content
        assert str(install_root / "app-1.5.6" / "LoLReview.exe") in batch_content
        assert ":launch" in batch_content
        assert "retries" in batch_content
        assert 'del "%~f0"' in batch_content

        # ── Step 4: post_update_startup detects the pending update ──
        with patch("src.updater.get_sxs_root", return_value=install_root), \
             patch("src.updater.__version__", "1.5.6"):
            result = post_update_startup()
            assert result is True
            # .update_pending should be cleaned up
            assert not (install_root / ".update_pending").exists()

    @patch("src.updater._get_install_root")
    @patch("src.updater.requests.get")
    @patch("src.updater.__version__", "1.5.5")
    def test_already_installed_skips_download(self, mock_get, mock_root, tmp_path):
        """If update is already installed locally, skip download and just restart."""
        install_root = self._make_install_root(tmp_path)
        mock_root.return_value = install_root

        # Pre-install the new version (simulating a previous failed restart)
        new_app = install_root / "app-1.5.6"
        new_app.mkdir()
        (new_app / "LoLReview.exe").touch()

        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.json.return_value = {
            "tag_name": "v1.5.6",
            "assets": [
                {
                    "name": "LoLReview-v1.5.6.zip",
                    "browser_download_url": "https://example.com/dl.zip",
                }
            ],
        }
        mock_get.return_value = mock_resp

        update_info = check_for_update()
        assert update_info is not None
        assert update_info["already_installed"] is True

        # At this point the app would call set_current_version + restart,
        # NOT download_and_install
        set_current_version("1.5.6")
        assert (install_root / ".current").read_text(encoding="utf-8") == "1.5.6"
        assert (install_root / ".update_pending").exists()

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    @patch("src.updater.requests.get")
    @patch("src.updater.__version__", "1.5.5")
    def test_cleanup_removes_old_versions_after_update(
        self, mock_get, mock_root, mock_popen, tmp_path
    ):
        """After a successful update, cleanup should remove old versions
        except current and previous."""
        install_root = self._make_install_root(tmp_path, current_version="1.5.3")
        mock_root.return_value = install_root

        # Add more old versions
        for v in ["1.5.4", "1.5.5"]:
            d = install_root / f"app-{v}"
            d.mkdir()
            (d / "LoLReview.exe").touch()

        # Install new version
        zip_path = self._make_update_zip(tmp_path, "1.5.6")
        zip_data = zip_path.read_bytes()

        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.headers = {"content-length": str(len(zip_data))}
        mock_resp.iter_content = MagicMock(return_value=[zip_data])
        mock_resp.raise_for_status = MagicMock()
        mock_get.return_value = mock_resp

        _do_download_and_install("https://example.com/dl.zip", "v1.5.6")

        # .current=1.5.6, .previous=1.5.3
        assert (install_root / ".current").read_text(encoding="utf-8") == "1.5.6"
        assert (install_root / ".previous").read_text(encoding="utf-8") == "1.5.3"

        # Run cleanup
        with patch("src.updater.get_sxs_root", return_value=install_root):
            cleanup_old_versions()

        # Current and previous kept, others removed
        assert (install_root / "app-1.5.6").exists()
        assert (install_root / "app-1.5.3").exists()
        assert not (install_root / "app-1.5.4").exists()
        assert not (install_root / "app-1.5.5").exists()


class TestBatchScriptReliability:
    """Tests focused on the batch script content being correct and robust."""

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    def test_batch_polls_for_correct_pid(self, mock_root, mock_popen, tmp_path):
        mock_root.return_value = tmp_path
        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        restart_into_version("1.5.6")

        content = (tmp_path / "_restart.cmd").read_text(encoding="mbcs")
        pid = str(os.getpid())

        # PID should appear in the tasklist filter, NOT in a raw find
        assert f'PID eq {pid}' in content
        # Should NOT use old unreliable `find "{pid}"` pattern
        assert f'find "{pid}"' not in content

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    def test_batch_launches_correct_exe_path(self, mock_root, mock_popen, tmp_path):
        mock_root.return_value = tmp_path
        app_dir = tmp_path / "app-2.0.0"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("2.0.0", encoding="utf-8")

        restart_into_version("2.0.0")

        content = (tmp_path / "_restart.cmd").read_text(encoding="mbcs")
        expected_exe = str(tmp_path / "app-2.0.0" / "LoLReview.exe")
        assert expected_exe in content

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    def test_batch_self_deletes(self, mock_root, mock_popen, tmp_path):
        mock_root.return_value = tmp_path
        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        restart_into_version("1.5.6")

        content = (tmp_path / "_restart.cmd").read_text(encoding="mbcs")
        assert 'del "%~f0"' in content

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    def test_batch_structure_order(self, mock_root, mock_popen, tmp_path):
        """Verify the batch script has the right structure: wait loop → launch → cleanup."""
        mock_root.return_value = tmp_path
        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        restart_into_version("1.5.6")

        content = (tmp_path / "_restart.cmd").read_text(encoding="mbcs")
        lines = [l.strip() for l in content.splitlines() if l.strip()]

        # Basic structural assertions
        assert lines[0] == "@echo off"
        # :wait label should come before :launch label
        wait_idx = next(i for i, l in enumerate(lines) if l == ":wait")
        launch_idx = next(i for i, l in enumerate(lines) if l == ":launch")
        assert wait_idx < launch_idx
        # start command should come after :launch
        start_idx = next(i for i, l in enumerate(lines) if "start" in l and "LoLReview" in l)
        assert start_idx > launch_idx
        # del should be the last line
        assert 'del "%~f0"' in lines[-1]

    @patch("src.updater.subprocess.Popen", side_effect=OSError("Access denied"))
    @patch("src.updater._get_install_root")
    def test_popen_failure_returns_false_and_cleans_up(self, mock_root, mock_popen, tmp_path):
        """If Popen fails (e.g. antivirus blocks cmd.exe), return False and clean up."""
        mock_root.return_value = tmp_path
        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        result = restart_into_version("1.5.6")

        assert result is False
        # Batch file should be cleaned up on Popen failure
        assert not (tmp_path / "_restart.cmd").exists()


class TestRestartForUpdateIntegration:
    """Tests for _restart_for_update behavior in main.py (mocked at module boundaries)."""

    @patch("src.updater.subprocess.Popen")
    @patch("src.updater._get_install_root")
    def test_restart_succeeds_when_exe_exists(self, mock_root, mock_popen, tmp_path):
        """restart_into_version should return True when everything is in place."""
        mock_root.return_value = tmp_path
        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        (app_dir / "LoLReview.exe").touch()
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        result = restart_into_version("1.5.6")
        assert result is True

    @patch("src.updater._get_install_root")
    def test_restart_fails_gracefully_when_exe_missing(self, mock_root, tmp_path):
        """restart_into_version returns False (not crash) when target exe is gone."""
        mock_root.return_value = tmp_path
        app_dir = tmp_path / "app-1.5.6"
        app_dir.mkdir()
        # No exe — maybe antivirus quarantined it
        (tmp_path / ".current").write_text("1.5.6", encoding="utf-8")

        result = restart_into_version("1.5.6")
        assert result is False

    @patch("src.updater._get_install_root")
    def test_restart_fails_when_install_root_none(self, mock_root):
        """Dev mode: no install root → False."""
        mock_root.return_value = None
        result = restart_into_version("1.5.6")
        assert result is False
