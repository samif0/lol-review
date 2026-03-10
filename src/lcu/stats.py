"""Extract structured stats from end-of-game data."""

import logging
import time
from typing import Optional

from .models import GameStats

logger = logging.getLogger(__name__)


def extract_stats_from_eog(eog_data: dict) -> Optional[GameStats]:
    """Extract structured stats from the end-of-game stats block.

    The EOG block has a different structure than match history data,
    so we handle both paths.
    """
    if not eog_data:
        return None

    try:
        local_player = eog_data.get("localPlayer", {})
        stats_block = local_player.get("stats", {})
        game_id = eog_data.get("gameId", 0)
        game_length = eog_data.get("gameLength", 0)
        game_mode = eog_data.get("gameMode", "")
        game_type = eog_data.get("gameType", "")

        # Find player's team
        team_id = local_player.get("teamId", 100)

        kills = int(stats_block.get("CHAMPIONS_KILLED", 0))
        deaths = int(stats_block.get("NUM_DEATHS", 0))
        assists = int(stats_block.get("ASSISTS", 0))
        minions = int(stats_block.get("MINIONS_KILLED", 0))
        jungle = int(stats_block.get("NEUTRAL_MINIONS_KILLED", 0))
        cs_total = minions + jungle
        duration_min = max(game_length / 60, 1)

        # Sum kills of all players on our team for kill participation
        team_kills_total = 0
        for team_data in eog_data.get("teams", []):
            if team_data.get("teamId") == team_id:
                for p in team_data.get("players", []):
                    p_stats = p.get("stats", {})
                    team_kills_total += int(p_stats.get("CHAMPIONS_KILLED", 0))

        kda = (kills + assists) / max(deaths, 1)
        kp = (kills + assists) / max(team_kills_total, 1) * 100

        gs = GameStats(
            game_id=game_id,
            timestamp=int(time.time()),
            game_duration=game_length,
            game_mode=game_mode,
            game_type=game_type,
            champion_name=local_player.get("championName", "Unknown"),
            champion_id=local_player.get("championId", 0),
            team_id=team_id,
            position=local_player.get("selectedPosition", ""),
            win=local_player.get("stats", {}).get("WIN", "0") != "0",
            kills=kills,
            deaths=deaths,
            assists=assists,
            kda_ratio=round(kda, 2),
            largest_killing_spree=int(stats_block.get("LARGEST_KILLING_SPREE", 0)),
            largest_multi_kill=int(stats_block.get("LARGEST_MULTI_KILL", 0)),
            double_kills=int(stats_block.get("DOUBLE_KILLS", 0)),
            triple_kills=int(stats_block.get("TRIPLE_KILLS", 0)),
            quadra_kills=int(stats_block.get("QUADRA_KILLS", 0)),
            penta_kills=int(stats_block.get("PENTA_KILLS", 0)),
            total_damage_dealt=int(stats_block.get("TOTAL_DAMAGE_DEALT", 0)),
            total_damage_to_champions=int(stats_block.get("TOTAL_DAMAGE_DEALT_TO_CHAMPIONS", 0)),
            physical_damage_to_champions=int(stats_block.get("PHYSICAL_DAMAGE_DEALT_TO_CHAMPIONS", 0)),
            magic_damage_to_champions=int(stats_block.get("MAGIC_DAMAGE_DEALT_TO_CHAMPIONS", 0)),
            true_damage_to_champions=int(stats_block.get("TRUE_DAMAGE_DEALT_TO_CHAMPIONS", 0)),
            total_damage_taken=int(stats_block.get("TOTAL_DAMAGE_TAKEN", 0)),
            damage_self_mitigated=int(stats_block.get("DAMAGE_SELF_MITIGATED", 0)),
            largest_critical_strike=int(stats_block.get("LARGEST_CRITICAL_STRIKE", 0)),
            gold_earned=int(stats_block.get("GOLD_EARNED", 0)),
            gold_spent=int(stats_block.get("GOLD_SPENT", 0)),
            total_minions_killed=minions,
            neutral_minions_killed=jungle,
            cs_total=cs_total,
            cs_per_min=round(cs_total / duration_min, 1),
            vision_score=int(stats_block.get("VISION_SCORE", 0)),
            wards_placed=int(stats_block.get("WARD_PLACED", 0)),
            wards_killed=int(stats_block.get("WARD_KILLED", 0)),
            control_wards_purchased=int(stats_block.get("VISION_WARDS_BOUGHT_IN_GAME", 0)),
            turret_kills=int(stats_block.get("TURRETS_KILLED", 0)),
            inhibitor_kills=int(stats_block.get("BARRACKS_KILLED", 0)),
            total_heal=int(stats_block.get("TOTAL_HEAL", 0)),
            total_heals_on_teammates=int(stats_block.get("TOTAL_HEAL_ON_TEAMMATES", 0)),
            total_time_cc_dealt=int(stats_block.get("TOTAL_TIME_CROWD_CONTROL_DEALT", 0)),
            time_ccing_others=int(stats_block.get("TIME_CCING_OTHERS", 0)),
            spell1_casts=int(stats_block.get("SPELL1_CAST", 0)),
            spell2_casts=int(stats_block.get("SPELL2_CAST", 0)),
            spell3_casts=int(stats_block.get("SPELL3_CAST", 0)),
            spell4_casts=int(stats_block.get("SPELL4_CAST", 0)),
            champ_level=int(stats_block.get("LEVEL", 0)),
            team_kills=team_kills_total,
            kill_participation=round(kp, 1),
            items=[
                int(stats_block.get(f"ITEM{i}", 0))
                for i in range(7)
                if int(stats_block.get(f"ITEM{i}", 0)) != 0
            ],
            raw_stats=stats_block,
        )

        # Try to get summoner name from the data
        gs.summoner_name = eog_data.get("localPlayer", {}).get(
            "summonerName", "Unknown"
        )

        return gs

    except Exception as e:
        logger.error(f"Failed to extract EOG stats: {e}", exc_info=True)
        return None
