#nullable enable

using System;
using System.Collections.Generic;

namespace Revu.Core.Lcu;

/// <summary>
/// Brute-force max-likelihood role assignment for a 5-player team.
///
/// LCU does not expose <c>assignedPosition</c> on the enemy team during champ
/// select, so we have to infer the role layout from champion identity alone.
/// Per-champion role weights live in <see cref="ChampionRolePriors"/>; this
/// class picks the assignment of (player → role) that maximizes the product of
/// per-player role likelihoods, subject to the constraint that each of the
/// five roles is taken exactly once.
///
/// We brute-force all 5! = 120 permutations because the search space is tiny
/// and a clean Hungarian implementation costs more code than it saves.
/// </summary>
internal static class RoleAssignment
{
    /// <summary>Number of canonical Summoner's Rift roles.</summary>
    public const int RoleCount = 5;

    /// <summary>
    /// Given up to five champion names, return an array of length 5 mapping
    /// role index → champion name (using the same role indices as
    /// <see cref="ChampionRolePriors"/>: 0=Top, 1=Jg, 2=Mid, 3=Bot, 4=Supp).
    ///
    /// Empty/null entries in <paramref name="champions"/> are treated as
    /// "unknown" — they receive uniform role weights, so the solver assigns
    /// them to whatever role the other four didn't claim. The output role
    /// slot for an unknown champion will be an empty string.
    ///
    /// If <paramref name="champions"/> has fewer than 5 entries, the missing
    /// roles in the output are empty strings.
    /// </summary>
    public static string[] AssignRoles(IReadOnlyList<string> champions)
    {
        var output = new string[RoleCount];
        for (int i = 0; i < RoleCount; i++) output[i] = "";
        if (champions is null || champions.Count == 0) return output;

        // Pull weights for each champion. Pad with uniform rows if fewer
        // than 5 champions were supplied so the permutation search has a
        // square 5×5 matrix to work with.
        var weights = new double[RoleCount][];
        var names = new string[RoleCount];
        for (int i = 0; i < RoleCount; i++)
        {
            if (i < champions.Count && !string.IsNullOrWhiteSpace(champions[i]))
            {
                weights[i] = ChampionRolePriors.GetWeights(champions[i]);
                names[i] = champions[i];
            }
            else
            {
                weights[i] = ChampionRolePriors.Uniform;
                names[i] = "";
            }
        }

        var perm = new int[RoleCount] { 0, 1, 2, 3, 4 };
        var bestPerm = new int[RoleCount] { 0, 1, 2, 3, 4 };
        var best = new double[] { double.NegativeInfinity };

        Permute(perm, 0, weights, bestPerm, best);

        // bestPerm[playerIndex] = roleIndex. Invert so output[roleIndex] = name.
        for (int playerIdx = 0; playerIdx < RoleCount; playerIdx++)
        {
            var roleIdx = bestPerm[playerIdx];
            output[roleIdx] = names[playerIdx];
        }
        return output;
    }

    /// <summary>Recursive permutation generator. At each leaf, score the
    /// current <paramref name="perm"/> and update <paramref name="bestPerm"/>
    /// if it beats the current best.</summary>
    private static void Permute(int[] perm, int start, double[][] weights, int[] bestPerm, double[] best)
    {
        if (start == perm.Length - 1)
        {
            var score = ScorePermutation(weights, perm);
            if (score > best[0])
            {
                best[0] = score;
                Array.Copy(perm, bestPerm, perm.Length);
            }
            return;
        }

        for (int i = start; i < perm.Length; i++)
        {
            (perm[start], perm[i]) = (perm[i], perm[start]);
            Permute(perm, start + 1, weights, bestPerm, best);
            (perm[start], perm[i]) = (perm[i], perm[start]);
        }
    }

    /// <summary>
    /// Sum of log-weights for an assignment. A zero weight maps to negative
    /// infinity, so any permutation that places a "never-here" champion in
    /// that role is dominated. Uniform-weight (unknown) rows contribute the
    /// same constant to every permutation, so they don't bias the choice.
    /// </summary>
    private static double ScorePermutation(double[][] weights, int[] perm)
    {
        double total = 0.0;
        for (int playerIdx = 0; playerIdx < RoleCount; playerIdx++)
        {
            var roleIdx = perm[playerIdx];
            var w = weights[playerIdx][roleIdx];
            if (w <= 0) return double.NegativeInfinity;
            total += Math.Log(w);
        }
        return total;
    }
}
