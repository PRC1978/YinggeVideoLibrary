namespace YinggeVideoLibrary;

internal static class JavDatabaseActorSearchDiagnostics
{
    const string ActorUrl = "https://www.javdatabase.com/idols/performer-one/";
    const string MovieUrl = "https://www.javdatabase.com/movies/abc-123/";
    static readonly JavDatabaseActorSearchHit Hit = new("Performer One", ActorUrl);

    static string Search(string canonical = "https://www.javdatabase.com/?s=Performer%20One",
        string heading = "Search Results for: <span>Performer One</span>") =>
        "<html><head><link rel='canonical' href='" + canonical + "'></head><body>" +
        "<h1>" + heading + "</h1>" +
        "<p class='display-6 pcard'><a href='" + ActorUrl + "'>Performer One</a></p>" +
        "<p class='display-6 pcard'><a href='https://www.javdatabase.com/idols/performer-two/'>Performer Two</a></p>" +
        "<p class='display-6 pcard'><a href='https://www.javdatabase.com.evil.test/idols/performer-one/'>Performer One</a></p>" +
        new string('x', 550) + "</body></html>";

    static string Profile(string canonical = ActorUrl, string heading = "Performer One - JAV Profile") =>
        "<html><head><link rel='canonical' href='" + canonical + "'></head><body>" +
        "<h1 class='idol-name'>" + heading + "</h1>" +
        "<div class='card borderlesscard'><p class='pcard'><a href='https://example.test/ad'>Sponsored</a></p></div>" +
        "<div class='card borderlesscard'><p class='pcard'><a href='" + MovieUrl + "'>ABC-123</a></p>" +
        "<div class='mt-auto'><a href='" + MovieUrl + "'>Example title</a></div></div>" +
        new string('x', 550) + "</body></html>";

    static string Detail(string actorUrl = ActorUrl, string actorName = "Performer One") =>
        "<html><h1>ABC-123 Example title</h1>" +
        "<p class='mb-1'><b>DVD ID: </b>ABC-123</p>" +
        "<p class='mb-1'><b>Idol(s)/Actress(es): </b><span><a href='" + actorUrl + "'>" + actorName + "</a></span></p>" +
        new string('x', 550) + "</html>";

    internal static Dictionary<string, bool> Verify()
    {
        var hits = JavDatabaseCatalog.ParseActorSearchPage("Performer One", Search());
        var movies = JavDatabaseCatalog.ParseVerifiedActorProfile(Hit, Profile());
        var verified = JavDatabaseCatalog.ExtractVerifiedCatalogLinks(movies[0], Detail());
        var empty = JavDatabaseCatalog.ParseActorSearchPage("Performer One",
            "<html><head><link rel='canonical' href='https://www.javdatabase.com/?s=Performer%20One'></head>" +
            "<main>No results found. Please adjust your search term.</main>" + new string('x', 550) + "</html>");
        return new Dictionary<string, bool>
        {
            ["nativeActorNameValidated"] = JavDatabaseCatalog.TryActorSearchName("Performer One", out _) &&
                !JavDatabaseCatalog.TryActorSearchName("C:\\videos\\actor", out _) &&
                !JavDatabaseCatalog.TryActorSearchName("https://example.test", out _),
            ["nativeActorSearchExactCardOnly"] = hits.Count == 1 && hits[0].Name == "Performer One" &&
                hits[0].Url == ActorUrl && empty.Count == 0,
            ["nativeActorSearchPageIdentityRejected"] = Rejects(() => JavDatabaseCatalog.ParseActorSearchPage(
                    "Performer One", Search(canonical: "https://www.javdatabase.com.evil.test/?s=Performer%20One"))) &&
                Rejects(() => JavDatabaseCatalog.ParseActorSearchPage("Performer One",
                    Search(canonical: "https://www.javdatabase.com/?s=Performer%20One&amp;next=evil"))) &&
                Rejects(() => JavDatabaseCatalog.ParseActorSearchPage("Performer One",
                    Search(heading: "Search Results for: Another Person"))),
            ["nativeActorProfileIdentityRejected"] = Rejects(() => JavDatabaseCatalog.ParseVerifiedActorProfile(Hit,
                    Profile(canonical: "https://www.javdatabase.com/idols/other-person/"))) &&
                Rejects(() => JavDatabaseCatalog.ParseVerifiedActorProfile(Hit,
                    Profile(heading: "Other Person - JAV Profile"))),
            ["nativeActorSearchNeedsVerifiedMovie"] = movies.Count == 1 && movies[0].Code == "ABC-123" &&
                !movies[0].DetailFetched && !movies[0].DetailIdentityVerified &&
                verified.Count == 1 && verified[0].OriginCode == "ABC-123" &&
                verified[0].OriginMovieUrl == MovieUrl && verified[0].Url == ActorUrl,
            // A detail can truthfully mention another performer; that must
            // never count as evidence for the searched actor.
            ["nativeActorSearchRejectsUnverifiedRelationship"] = JavDatabaseCatalog.ExtractVerifiedCatalogLinks(movies[0],
                Detail(actorUrl: "https://www.javdatabase.com/idols/other-person/", actorName: "Other Person"))
                .All(link => link.Url != ActorUrl)
        };
    }

    static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is InvalidOperationException or ScrapeNotFoundException or ArgumentException)
        { return true; }
    }
}
