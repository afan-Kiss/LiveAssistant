using LiveAssistant.Models;

namespace LiveAssistant.Utils;

public static class ArtistNameMatcher
{
    public static SongSearchCandidate? Match(string input, IReadOnlyList<SongSearchCandidate> candidates)
    {
        input = input.Trim();
        if (input.Length == 0 || candidates.Count == 0)
        {
            return null;
        }

        var exact = candidates.FirstOrDefault(c =>
            c.Artist.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        var partial = candidates
            .Where(c =>
                c.Artist.Contains(input, StringComparison.OrdinalIgnoreCase)
                || input.Contains(c.Artist, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    public static string FormatArtistList(IReadOnlyList<SongSearchCandidate> candidates)
        => string.Join("、", candidates.Select(c => c.Artist));
}
