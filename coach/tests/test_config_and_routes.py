from fastapi.testclient import TestClient


def test_save_config_does_not_write_api_keys(isolated_user_data):
    _, config, _ = isolated_user_data

    cfg = config.CoachConfig(
        google_ai=config.GoogleAIConfig(api_key="google-secret"),
        openrouter=config.OpenRouterConfig(api_key="openrouter-secret"),
    )

    config.save_config(cfg)

    raw = config.config_path().read_text(encoding="utf-8")
    assert "google-secret" not in raw
    assert "openrouter-secret" not in raw


def test_config_route_omits_runtime_api_keys(isolated_user_data):
    _, config, db = isolated_user_data

    import coach.main as main

    config._current_config = config.CoachConfig(
        google_ai=config.GoogleAIConfig(api_key="google-secret"),
        openrouter=config.OpenRouterConfig(api_key="openrouter-secret"),
    )
    db._migrations_applied = False

    with TestClient(main.app) as client:
        response = client.get("/config")

    assert response.status_code == 200
    payload = response.json()
    assert "api_key" not in payload["google_ai"]
    assert "api_key" not in payload["openrouter"]
