#nullable enable

using System.Collections.Generic;

namespace Revu.Core.Lcu;

/// <summary>
/// Per-champion role-likelihood weights for the five Summoner's Rift positions.
/// Used to assign enemy roles in champ select, where Riot's LCU does not expose
/// <c>assignedPosition</c> on the enemy team.
///
/// Weights are non-negative and roughly sum to 1.0 per row, but the role
/// assignment uses log-likelihood comparisons so the absolute scale is not
/// load-bearing — only the ratio between roles within a row matters. A value
/// of 0 means "essentially never picked here" and is used as a hard exclusion.
///
/// Values reflect ranked-solo meta as of patch 14.x (NA/EUW Emerald+). Refresh
/// when a major patch shifts a flex pick (Senna ADC↔supp, Pyke top↔supp,
/// Karma supp↔mid, etc.). Unknown champions fall back to uniform weights so
/// the assignment still works for new releases — just less precisely.
/// </summary>
internal static class ChampionRolePriors
{
    /// <summary>Role indices match <see cref="LcuClient.RoleToIndex"/>:
    /// 0=Top, 1=Jungle, 2=Mid, 3=Bottom (ADC), 4=Utility (Support).</summary>
    public const int Top = 0;
    public const int Jungle = 1;
    public const int Mid = 2;
    public const int Bot = 3;
    public const int Supp = 4;

    /// <summary>Uniform fallback for unknown champions.</summary>
    public static readonly double[] Uniform = [0.20, 0.20, 0.20, 0.20, 0.20];

    /// <summary>
    /// Look up role weights by champion display name (case-insensitive,
    /// punctuation-insensitive — "Kai'Sa" / "Kaisa" / "kaisa" all match).
    /// Returns <see cref="Uniform"/> for unknown names so callers don't need
    /// a null-check.
    /// </summary>
    public static double[] GetWeights(string championName)
    {
        if (string.IsNullOrWhiteSpace(championName)) return Uniform;
        var key = Normalize(championName);
        return Priors.TryGetValue(key, out var w) ? w : Uniform;
    }

    private static string Normalize(string name)
    {
        // Strip apostrophes/spaces/dots so 'Kai'Sa', 'Kaisa', 'Cho'Gath',
        // 'Dr. Mundo', 'Wukong', etc. all hit the same key.
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    // Format: { "normalizedname", [top, jg, mid, bot, supp] }.
    // Sourced from current-meta consensus (u.gg / lolalytics primary-role
    // distributions at Emerald+). Numbers are eyeballed proportions, not
    // exact play-rates — within ~10% is fine since the assignment only
    // needs ordinal correctness across the 5 enemies.
    private static readonly Dictionary<string, double[]> Priors = new()
    {
        // ── Top mains ────────────────────────────────────────────────
        ["aatrox"]      = [0.85, 0.05, 0.10, 0.00, 0.00],
        ["camille"]     = [0.85, 0.05, 0.10, 0.00, 0.00],
        ["chogath"]     = [0.70, 0.05, 0.20, 0.00, 0.05],
        ["darius"]      = [0.95, 0.03, 0.02, 0.00, 0.00],
        ["drmundo"]     = [0.85, 0.10, 0.05, 0.00, 0.00],
        ["fiora"]       = [0.95, 0.02, 0.03, 0.00, 0.00],
        ["gangplank"]   = [0.85, 0.02, 0.13, 0.00, 0.00],
        ["garen"]       = [0.92, 0.03, 0.05, 0.00, 0.00],
        ["gnar"]        = [0.92, 0.03, 0.05, 0.00, 0.00],
        ["gwen"]        = [0.80, 0.10, 0.10, 0.00, 0.00],
        ["illaoi"]      = [0.95, 0.02, 0.03, 0.00, 0.00],
        ["irelia"]      = [0.55, 0.02, 0.43, 0.00, 0.00],
        ["jax"]         = [0.65, 0.30, 0.05, 0.00, 0.00],
        ["jayce"]       = [0.55, 0.02, 0.43, 0.00, 0.00],
        ["kennen"]      = [0.85, 0.02, 0.13, 0.00, 0.00],
        ["kled"]        = [0.95, 0.05, 0.00, 0.00, 0.00],
        ["malphite"]    = [0.75, 0.05, 0.15, 0.00, 0.05],
        ["mordekaiser"] = [0.80, 0.05, 0.15, 0.00, 0.00],
        ["nasus"]       = [0.92, 0.05, 0.03, 0.00, 0.00],
        ["olaf"]        = [0.30, 0.65, 0.05, 0.00, 0.00],
        ["ornn"]        = [0.95, 0.05, 0.00, 0.00, 0.00],
        ["pantheon"]    = [0.40, 0.05, 0.30, 0.00, 0.25],
        ["poppy"]       = [0.55, 0.30, 0.05, 0.00, 0.10],
        ["quinn"]       = [0.85, 0.05, 0.10, 0.00, 0.00],
        ["renekton"]    = [0.92, 0.03, 0.05, 0.00, 0.00],
        ["riven"]       = [0.92, 0.03, 0.05, 0.00, 0.00],
        ["rumble"]      = [0.55, 0.10, 0.35, 0.00, 0.00],
        ["sett"]        = [0.55, 0.05, 0.05, 0.00, 0.35],
        ["shen"]        = [0.85, 0.10, 0.05, 0.00, 0.00],
        ["singed"]      = [0.92, 0.05, 0.03, 0.00, 0.00],
        ["sion"]        = [0.85, 0.05, 0.10, 0.00, 0.00],
        ["tahmkench"]   = [0.55, 0.02, 0.03, 0.00, 0.40],
        ["teemo"]       = [0.80, 0.02, 0.13, 0.05, 0.00],
        ["trundle"]     = [0.55, 0.40, 0.05, 0.00, 0.00],
        ["tryndamere"]  = [0.92, 0.05, 0.03, 0.00, 0.00],
        ["urgot"]       = [0.95, 0.02, 0.03, 0.00, 0.00],
        ["volibear"]    = [0.55, 0.40, 0.05, 0.00, 0.00],
        ["warwick"]     = [0.20, 0.75, 0.05, 0.00, 0.00],
        ["yorick"]      = [0.95, 0.03, 0.02, 0.00, 0.00],

        // ── Jungle mains ─────────────────────────────────────────────
        ["amumu"]       = [0.05, 0.85, 0.05, 0.00, 0.05],
        ["belveth"]     = [0.02, 0.95, 0.03, 0.00, 0.00],
        ["briar"]       = [0.02, 0.95, 0.03, 0.00, 0.00],
        ["diana"]       = [0.02, 0.45, 0.53, 0.00, 0.00],
        ["ekko"]        = [0.02, 0.30, 0.65, 0.00, 0.03],
        ["elise"]       = [0.02, 0.92, 0.06, 0.00, 0.00],
        ["evelynn"]     = [0.02, 0.95, 0.03, 0.00, 0.00],
        ["fiddlesticks"]= [0.02, 0.95, 0.03, 0.00, 0.00],
        ["graves"]      = [0.05, 0.90, 0.03, 0.02, 0.00],
        ["hecarim"]     = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["ivern"]       = [0.02, 0.95, 0.00, 0.00, 0.03],
        ["jarvaniv"]    = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["karthus"]     = [0.02, 0.85, 0.13, 0.00, 0.00],
        ["kayn"]        = [0.02, 0.95, 0.03, 0.00, 0.00],
        ["khazix"]      = [0.02, 0.95, 0.03, 0.00, 0.00],
        ["kindred"]     = [0.02, 0.95, 0.03, 0.00, 0.00],
        ["leesin"]      = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["lillia"]      = [0.05, 0.85, 0.10, 0.00, 0.00],
        ["masteryi"]    = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["nidalee"]     = [0.02, 0.95, 0.03, 0.00, 0.00],
        ["nocturne"]    = [0.02, 0.92, 0.06, 0.00, 0.00],
        ["nunu"]        = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["nunuwillump"] = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["rammus"]      = [0.02, 0.95, 0.03, 0.00, 0.00],
        ["reksai"]      = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["rengar"]      = [0.10, 0.85, 0.05, 0.00, 0.00],
        ["sejuani"]     = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["shaco"]       = [0.02, 0.95, 0.03, 0.00, 0.00],
        ["shyvana"]     = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["skarner"]     = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["taliyah"]     = [0.02, 0.55, 0.43, 0.00, 0.00],
        ["udyr"]        = [0.05, 0.90, 0.03, 0.00, 0.02],
        ["vi"]          = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["viego"]       = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["wukong"]      = [0.55, 0.40, 0.05, 0.00, 0.00],
        ["xinzhao"]     = [0.05, 0.92, 0.03, 0.00, 0.00],
        ["zac"]         = [0.05, 0.92, 0.03, 0.00, 0.00],

        // ── Mid mains ────────────────────────────────────────────────
        ["ahri"]        = [0.00, 0.02, 0.95, 0.00, 0.03],
        ["akali"]       = [0.20, 0.02, 0.78, 0.00, 0.00],
        ["akshan"]      = [0.05, 0.02, 0.85, 0.08, 0.00],
        ["anivia"]      = [0.00, 0.02, 0.95, 0.00, 0.03],
        ["annie"]       = [0.00, 0.00, 0.55, 0.00, 0.45],
        ["aurelionsol"] = [0.00, 0.02, 0.95, 0.00, 0.03],
        ["aurora"]      = [0.20, 0.02, 0.78, 0.00, 0.00],
        ["azir"]        = [0.00, 0.02, 0.95, 0.00, 0.03],
        ["cassiopeia"]  = [0.10, 0.02, 0.85, 0.00, 0.03],
        ["fizz"]        = [0.00, 0.02, 0.95, 0.00, 0.03],
        ["galio"]       = [0.05, 0.02, 0.85, 0.00, 0.08],
        ["hwei"]        = [0.00, 0.02, 0.85, 0.00, 0.13],
        ["kassadin"]    = [0.00, 0.02, 0.95, 0.00, 0.03],
        ["katarina"]    = [0.05, 0.02, 0.92, 0.00, 0.01],
        ["leblanc"]     = [0.00, 0.02, 0.95, 0.00, 0.03],
        ["lissandra"]   = [0.05, 0.02, 0.90, 0.00, 0.03],
        ["lux"]         = [0.00, 0.02, 0.55, 0.00, 0.43],
        ["malzahar"]    = [0.00, 0.02, 0.95, 0.00, 0.03],
        ["naafiri"]     = [0.10, 0.05, 0.85, 0.00, 0.00],
        ["neeko"]       = [0.00, 0.02, 0.55, 0.00, 0.43],
        ["orianna"]     = [0.00, 0.02, 0.95, 0.00, 0.03],
        ["qiyana"]      = [0.00, 0.10, 0.90, 0.00, 0.00],
        ["ryze"]        = [0.05, 0.02, 0.85, 0.00, 0.00],
        ["sylas"]       = [0.10, 0.05, 0.85, 0.00, 0.00],
        ["syndra"]      = [0.00, 0.02, 0.85, 0.13, 0.00],
        ["talon"]       = [0.05, 0.10, 0.85, 0.00, 0.00],
        ["twistedfate"] = [0.10, 0.02, 0.83, 0.00, 0.05],
        ["veigar"]      = [0.00, 0.02, 0.85, 0.00, 0.13],
        ["vex"]         = [0.05, 0.02, 0.90, 0.00, 0.03],
        ["viktor"]      = [0.05, 0.02, 0.90, 0.00, 0.03],
        ["vladimir"]    = [0.30, 0.02, 0.65, 0.00, 0.03],
        ["xerath"]      = [0.00, 0.02, 0.55, 0.00, 0.43],
        ["yasuo"]       = [0.20, 0.02, 0.65, 0.13, 0.00],
        ["yone"]        = [0.30, 0.02, 0.65, 0.03, 0.00],
        ["zed"]         = [0.05, 0.02, 0.90, 0.03, 0.00],
        ["ziggs"]       = [0.00, 0.02, 0.55, 0.40, 0.03],
        ["zoe"]         = [0.00, 0.02, 0.95, 0.00, 0.03],

        // ── Bot/ADC mains ────────────────────────────────────────────
        ["aphelios"]    = [0.00, 0.00, 0.05, 0.95, 0.00],
        ["ashe"]        = [0.00, 0.00, 0.05, 0.90, 0.05],
        ["caitlyn"]     = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["draven"]      = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["ezreal"]      = [0.02, 0.02, 0.06, 0.90, 0.00],
        ["jhin"]        = [0.00, 0.00, 0.02, 0.95, 0.03],
        ["jinx"]        = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["kaisa"]       = [0.00, 0.00, 0.05, 0.95, 0.00],
        ["kalista"]     = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["kogmaw"]      = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["lucian"]      = [0.00, 0.00, 0.30, 0.70, 0.00],
        ["missfortune"] = [0.00, 0.00, 0.02, 0.93, 0.05],
        ["nilah"]       = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["samira"]      = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["sivir"]       = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["smolder"]     = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["tristana"]    = [0.00, 0.00, 0.10, 0.90, 0.00],
        ["twitch"]      = [0.00, 0.05, 0.02, 0.93, 0.00],
        ["varus"]       = [0.00, 0.00, 0.20, 0.78, 0.02],
        ["vayne"]       = [0.30, 0.00, 0.05, 0.65, 0.00],
        ["xayah"]       = [0.00, 0.00, 0.02, 0.98, 0.00],
        ["zeri"]        = [0.00, 0.00, 0.02, 0.98, 0.00],

        // ── Support mains ────────────────────────────────────────────
        ["alistar"]     = [0.05, 0.02, 0.00, 0.00, 0.93],
        ["bard"]        = [0.00, 0.02, 0.00, 0.00, 0.98],
        ["blitzcrank"]  = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["braum"]       = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["janna"]       = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["karma"]       = [0.00, 0.00, 0.20, 0.00, 0.80],
        ["leona"]       = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["lulu"]        = [0.05, 0.00, 0.05, 0.00, 0.90],
        ["maokai"]      = [0.20, 0.40, 0.00, 0.00, 0.40],
        ["milio"]       = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["morgana"]     = [0.05, 0.05, 0.10, 0.00, 0.80],
        ["nami"]        = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["nautilus"]    = [0.05, 0.05, 0.00, 0.00, 0.90],
        ["pyke"]        = [0.05, 0.00, 0.10, 0.00, 0.85],
        ["rakan"]       = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["rell"]        = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["renata"]      = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["renataglasc"] = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["senna"]       = [0.00, 0.00, 0.00, 0.40, 0.60],
        ["seraphine"]   = [0.00, 0.00, 0.10, 0.20, 0.70],
        ["sona"]        = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["soraka"]      = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["swain"]       = [0.05, 0.02, 0.20, 0.20, 0.53],
        ["taric"]       = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["thresh"]      = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["velkoz"]      = [0.00, 0.00, 0.40, 0.00, 0.60],
        ["yuumi"]       = [0.00, 0.00, 0.00, 0.00, 1.00],
        ["zilean"]      = [0.00, 0.00, 0.20, 0.00, 0.80],
        ["zyra"]        = [0.00, 0.00, 0.10, 0.00, 0.90],
    };
}
