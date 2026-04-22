namespace ion.compiler;

/// <summary>
/// Computes the Levenshtein (edit) distance between two strings.
/// Used to suggest likely type names when a reference is unresolved.
/// </summary>
public static class LevenshteinDistance
{
    public static int Compute(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var lenA = a.Length;
        var lenB = b.Length;

        // Single-row DP for O(min(m,n)) memory
        var prev = new int[lenB + 1];
        var curr = new int[lenB + 1];

        for (var j = 0; j <= lenB; j++)
            prev[j] = j;

        for (var i = 1; i <= lenA; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= lenB; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[lenB];
    }

    /// <summary>
    /// Find the closest matching name from a set of candidates.
    /// Returns null if no candidate is within the maximum edit distance.
    /// </summary>
    public static string? FindClosest(string target, IEnumerable<string> candidates, int maxDistance = 3)
    {
        string? best = null;
        var bestDist = int.MaxValue;

        foreach (var candidate in candidates)
        {
            // Quick length check to skip obviously different names
            if (Math.Abs(candidate.Length - target.Length) > maxDistance)
                continue;

            var dist = Compute(target, candidate);
            if (dist < bestDist && dist <= maxDistance && dist > 0)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        return best;
    }
}
