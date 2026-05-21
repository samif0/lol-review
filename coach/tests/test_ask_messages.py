from coach.modes.ask import _build_llm_messages


def test_build_llm_messages_places_current_question_last():
    messages = _build_llm_messages(
        [
            {"role": "user", "content": "first question"},
            {"role": "assistant", "content": "first answer"},
        ],
        "current grounded prompt",
    )

    assert [m.role for m in messages] == ["user", "assistant", "user"]
    assert messages[-1].content == "current grounded prompt"


def test_build_llm_messages_skips_empty_or_unknown_history():
    messages = _build_llm_messages(
        [
            {"role": "tool", "content": "ignore me"},
            {"role": "assistant", "content": ""},
            {"role": "user", "content": "keep me"},
        ],
        "current grounded prompt",
    )

    assert [(m.role, m.content) for m in messages] == [
        ("user", "keep me"),
        ("user", "current grounded prompt"),
    ]
