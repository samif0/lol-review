"""Data models for League game statistics."""

from dataclasses import dataclass, field


@dataclass
class GameStats:
    """Structured post-game statistics extracted from LCU data."""

    game_id: int = 0
    timestamp: int = 0
    game_duration: int = 0
    game_mode: str = ""
    game_type: str = ""
    queue_type: str = ""
    map_id: int = 0

    # Player info
    summoner_name: str = ""
    champion_name: str = ""
    champion_id: int = 0
    team_id: int = 0  # 100 = blue, 200 = red
    position: str = ""
    role: str = ""
    enemy_laner: str = ""

    # Outcome
    win: bool = False

    # KDA
    kills: int = 0
    deaths: int = 0
    assists: int = 0
    kda_ratio: float = 0.0
    largest_killing_spree: int = 0
    largest_multi_kill: int = 0
    double_kills: int = 0
    triple_kills: int = 0
    quadra_kills: int = 0
    penta_kills: int = 0
    first_blood: bool = False

    # Damage
    total_damage_dealt: int = 0
    total_damage_to_champions: int = 0
    physical_damage_to_champions: int = 0
    magic_damage_to_champions: int = 0
    true_damage_to_champions: int = 0
    total_damage_taken: int = 0
    damage_self_mitigated: int = 0
    largest_critical_strike: int = 0

    # Economy
    gold_earned: int = 0
    gold_spent: int = 0
    total_minions_killed: int = 0
    neutral_minions_killed: int = 0
    cs_total: int = 0
    cs_per_min: float = 0.0

    # Vision
    vision_score: int = 0
    wards_placed: int = 0
    wards_killed: int = 0
    control_wards_purchased: int = 0

    # Objectives
    turret_kills: int = 0
    inhibitor_kills: int = 0
    dragon_kills: int = 0
    baron_kills: int = 0
    rift_herald_kills: int = 0

    # Healing & Utility
    total_heal: int = 0
    total_heals_on_teammates: int = 0
    total_damage_shielded_on_teammates: int = 0
    total_time_cc_dealt: int = 0
    time_ccing_others: int = 0

    # Spells & Items
    spell1_casts: int = 0
    spell2_casts: int = 0
    spell3_casts: int = 0
    spell4_casts: int = 0
    summoner1_id: int = 0
    summoner2_id: int = 0
    items: list[int] = field(default_factory=list)

    # Level & XP
    champ_level: int = 0

    # Team totals (for context)
    team_kills: int = 0
    team_deaths: int = 0
    kill_participation: float = 0.0

    # Raw JSON for anything we might have missed
    raw_stats: dict = field(default_factory=dict)

    # Live events collected during the game (kills, deaths, objectives with timestamps)
    live_events: list[dict] = field(default_factory=list)
