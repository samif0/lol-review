import importlib

import pytest


@pytest.fixture()
def isolated_user_data(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))

    import coach.config as config
    import coach.db as db

    importlib.reload(config)
    importlib.reload(db)
    db._migrations_applied = False

    yield tmp_path, config, db

    db._migrations_applied = False
    config._current_config = None
