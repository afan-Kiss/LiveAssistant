using LiveAssistant.Models;

namespace LiveAssistant.Utils;

public static class ArtistNameMatcher
{
    private const int MinPartialInputLength = 2;

    public static SongSearchCandidate? Match(string input, IReadOnlyList<SongSearchCandidate> candidates)
    {
        input = input.Trim();
        if (input.Length == 0 || candidates.Count == 0)
        {
            return null;
        }

        var exact = candidates
            .Where(c => c.Artist.Equals(input, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1)
        {
            return HasAmbiguousArtistOverlap(exact[0].Artist, candidates) ? null : exact[0];
        }

        if (exact.Count > 1)
        {
            return null;
        }

        var normalizedInput = NormalizeArtist(input);
        if (normalizedInput.Length >= MinPartialInputLength)
        {
            var normalized = candidates
                .Where(c => NormalizeArtist(c.Artist).Equals(normalizedInput, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (normalized.Count == 1)
            {
                return HasAmbiguousArtistOverlap(normalized[0].Artist, candidates) ? null : normalized[0];
            }

            if (normalized.Count > 1)
            {
                return null;
            }
        }

        if (input.Length < MinPartialInputLength)
        {
            return null;
        }

        var partial = candidates
            .Where(c => IsPartialArtistMatch(input, c.Artist))
            .ToList();
        if (partial.Count != 1)
        {
            return null;
        }

        return HasAmbiguousArtistOverlap(partial[0].Artist, candidates) ? null : partial[0];
    }

    private static bool HasAmbiguousArtistOverlap(string matchedArtist, IReadOnlyList<SongSearchCandidate> candidates)
    {
        var normalizedMatched = NormalizeArtist(matchedArtist);
        if (normalizedMatched.Length < MinPartialInputLength)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Artist.Equals(matchedArtist, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var other = NormalizeArtist(candidate.Artist);
            if (other.Contains(normalizedMatched, StringComparison.OrdinalIgnoreCase)
                || normalizedMatched.Contains(other, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string FormatArtistList(IReadOnlyList<SongSearchCandidate> candidates)
        => string.Join("、", candidates.Select(c => c.Artist));

    private static bool IsPartialArtistMatch(string input, string artist)
    {
        if (artist.Contains(input, StringComparison.OrdinalIgnoreCase))
        {
            return input.Length >= MinPartialInputLength;
        }

        if (input.Contains(artist, StringComparison.OrdinalIgnoreCase))
        {
            return artist.Trim().Length >= MinPartialInputLength;
        }

        return false;
    }

    private static string NormalizeArtist(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var index = 0;
        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch) || ch is '.' or '·' or '-' or '_' or '/')
            {
                continue;
            }

            buffer[index++] = char.ToLowerInvariant(ch);
        }

        return index == 0 ? "" : new string(buffer[..index]);
    }
}
