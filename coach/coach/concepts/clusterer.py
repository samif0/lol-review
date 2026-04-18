"""HDBSCAN clustering over concept embeddings.

Plan §7 Phase 2 task 4. min_cluster_size=2, cosine distance, eps tunable.
"""

from __future__ import annotations

import logging
from collections import Counter

from coach.concepts.embedder import embed
from coach.db import read_core, write_coach

logger = logging.getLogger(__name__)

MIN_CLUSTER_SIZE = 2
CLUSTER_SELECTION_EPSILON = 0.15


def recluster() -> dict[str, int | None]:
    """Recluster every row in review_concepts. Writes cluster_id and concept_canonical."""
    with read_core() as conn:
        rows = conn.execute(
            "SELECT id, concept_raw FROM review_concepts WHERE concept_raw IS NOT NULL AND concept_raw != ''"
        ).fetchall()

    ids = [r["id"] for r in rows]
    texts = [str(r["concept_raw"]) for r in rows]

    if len(texts) < MIN_CLUSTER_SIZE:
        logger.info("Too few concepts (%d) to cluster; skipping.", len(texts))
        return {"clustered": 0, "singletons": len(texts), "clusters_found": 0}

    vectors = embed(texts)

    try:
        import hdbscan
    except Exception as exc:
        raise RuntimeError(f"hdbscan not available: {exc}") from exc

    clusterer = hdbscan.HDBSCAN(
        min_cluster_size=MIN_CLUSTER_SIZE,
        metric="euclidean",  # vectors are normalized so euclidean ~ cosine
        cluster_selection_epsilon=CLUSTER_SELECTION_EPSILON,
    )
    labels = clusterer.fit_predict(vectors)

    # For each cluster, pick canonical = shortest member that appears >= 2 times
    # across the cluster. Fall back to shortest if no repeat.
    from collections import defaultdict

    members: dict[int, list[tuple[int, str]]] = defaultdict(list)
    for id_, label, text in zip(ids, labels, texts):
        if label >= 0:
            members[int(label)].append((int(id_), text))

    canonical_per_cluster: dict[int, str] = {}
    for cluster_id, pairs in members.items():
        texts_in_cluster = [t for _, t in pairs]
        counts = Counter(texts_in_cluster)
        repeated = [t for t, n in counts.items() if n >= 2]
        candidates = repeated if repeated else list(set(texts_in_cluster))
        canonical = min(candidates, key=lambda s: (len(s), s))
        canonical_per_cluster[cluster_id] = canonical

    clustered = 0
    singletons = 0
    updates: list[tuple] = []
    for id_, label, text in zip(ids, labels, texts):
        if label >= 0:
            canon = canonical_per_cluster.get(int(label), text)
            updates.append((int(label), canon, int(id_)))
            clustered += 1
        else:
            updates.append((None, None, int(id_)))
            singletons += 1

    with write_coach() as conn:
        conn.executemany(
            """
            UPDATE review_concepts
            SET cluster_id = ?, concept_canonical = ?
            WHERE id = ?
            """,
            updates,
        )

    # After clustering, refresh the profile.
    from coach.concepts.profiler import reprofile

    reprofile()

    return {
        "clustered": clustered,
        "singletons": singletons,
        "clusters_found": len(canonical_per_cluster),
    }
