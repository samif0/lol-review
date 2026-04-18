"""Sentence-transformer embedding cache for concept clustering.

Cached in a Parquet file under %LOCALAPPDATA%\\LoLReviewData\\coach\\embeddings\\.
Plan §7 Phase 2 task 3.
"""

from __future__ import annotations

import logging
from pathlib import Path
from typing import Iterable

import numpy as np

from coach.config import embeddings_dir

logger = logging.getLogger(__name__)

EMBEDDING_MODEL_NAME = "sentence-transformers/all-MiniLM-L6-v2"
EMBEDDING_DIM = 384

_model = None
_cache_dirty = False
_cache_text_to_vec: dict[str, np.ndarray] = {}
_cache_file_loaded = False


def _parquet_path() -> Path:
    return embeddings_dir() / "concept_embeddings.parquet"


def _ensure_model():
    global _model
    if _model is not None:
        return _model
    try:
        from sentence_transformers import SentenceTransformer
    except Exception as exc:
        raise RuntimeError(
            f"sentence-transformers not available: {exc}. Phase 2 requires it."
        ) from exc
    _model = SentenceTransformer(EMBEDDING_MODEL_NAME)
    return _model


def _load_cache() -> None:
    global _cache_file_loaded
    if _cache_file_loaded:
        return
    p = _parquet_path()
    if p.exists():
        try:
            import pandas as pd

            df = pd.read_parquet(p)
            for _, row in df.iterrows():
                vec = np.array(row["vector"], dtype=np.float32)
                _cache_text_to_vec[row["text"]] = vec
            logger.info("Loaded %d cached embeddings from %s", len(_cache_text_to_vec), p)
        except Exception:
            logger.exception("Failed to load embeddings cache; starting empty")
    _cache_file_loaded = True


def _save_cache() -> None:
    global _cache_dirty
    if not _cache_dirty:
        return
    p = _parquet_path()
    p.parent.mkdir(parents=True, exist_ok=True)
    try:
        import pandas as pd

        rows = [
            {"text": text, "vector": vec.tolist()}
            for text, vec in _cache_text_to_vec.items()
        ]
        df = pd.DataFrame(rows)
        df.to_parquet(p, index=False)
        _cache_dirty = False
        logger.debug("Saved %d embeddings to %s", len(rows), p)
    except Exception:
        logger.exception("Failed to save embeddings cache")


def embed(texts: Iterable[str]) -> np.ndarray:
    """Return an (N, EMBEDDING_DIM) array for the given texts. Uses/updates cache."""
    _load_cache()
    texts_list = list(texts)
    if not texts_list:
        return np.zeros((0, EMBEDDING_DIM), dtype=np.float32)

    missing_indices: list[int] = []
    missing_texts: list[str] = []
    for i, t in enumerate(texts_list):
        if t not in _cache_text_to_vec:
            missing_indices.append(i)
            missing_texts.append(t)

    if missing_texts:
        global _cache_dirty
        model = _ensure_model()
        new_vecs = model.encode(missing_texts, convert_to_numpy=True, normalize_embeddings=True)
        for t, v in zip(missing_texts, new_vecs):
            _cache_text_to_vec[t] = v.astype(np.float32)
        _cache_dirty = True
        _save_cache()

    out = np.stack([_cache_text_to_vec[t] for t in texts_list])
    return out
