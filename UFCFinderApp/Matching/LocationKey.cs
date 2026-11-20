namespace UFCFinderApp.Matching;

/// <summary>
/// Builds the normalized venue identity used to decide whether a watch-list row is the
/// same business as a legal-list row. The abbreviations match the original app so
/// existing spreadsheets keep filtering exactly as they did before.
/// </summary>
public static class LocationKey
{
    public static string From(string? address, string? city, string? state, string? zip)
    {
        var key = (address ?? string.Empty).Trim().ToLowerInvariant()
                  + (city ?? string.Empty).Trim().ToLowerInvariant()
                  + (state ?? string.Empty).Trim().ToLowerInvariant()
                  + (zip ?? string.Empty).Trim().ToLowerInvariant();

        key = key.Replace(" ", string.Empty)
                 .Replace(",", string.Empty)
                 .Replace("-", string.Empty)
                 .Replace(".", string.Empty)
                 .Replace("#", string.Empty);

        return key
            .Replace("avenue", "ave")
            .Replace("street", "st")
            .Replace("circle", "cir")
            .Replace("boulevard", "blvd")
            .Replace("highway", "hwy")
            .Replace("parkway", "pkwy")
            .Replace("drive", "dr")
            .Replace("court", "ct")
            .Replace("terrace", "ter")
            .Replace("square", "sq")
            .Replace("suite", "ste")
            .Replace("apartment", "apt")
            .Replace("saint", "st")
            .Replace("road", "rd")
            .Replace("lane", "ln")
            .Replace("place", "pl");
    }

    public static string From(Models.LegalVenue venue) =>
        From(venue.Address, venue.City, venue.State, venue.Zip);

    public static string From(Models.WatchTarget target) =>
        From(target.Address, target.City, target.State, target.Zip);
}
