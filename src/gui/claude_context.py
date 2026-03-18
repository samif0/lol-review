"""Claude context generator — utility function."""


def generate_and_copy(db) -> str:
    """Generate context and return it. Caller handles clipboard."""
    context = db.generate_claude_context()
    return context
