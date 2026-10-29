using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// One BoardGameGeek search result: enough to tell editions apart in a choice list.
public class BggMatch
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? Year { get; set; }
}

// Thrown when BoardGameGeek can't answer. The message is written for the page to show as it is.
public class BggLookupException : Exception
{
    public BggLookupException(string message)
        : base(message)
    {
    }

    public BggLookupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

// Finds a Game on BoardGameGeek and reads its facts (S-07), for a Game the Catalog doesn't have.
// Two calls of the XML API 2: /search for the choice list, then /thing with stats for the one picked.
//
// The token (Bgg:Token, a Key Vault secret in Azure, never an app setting) is read on every call, so a
// rotated token is used without a restart, and with no token the page says lookup isn't set up.
//
// BGG is slow and rate-limited, so every call has a short timeout and is tried ONCE: a page's click
// handler must never sit in a retry loop. A 202 ("queued, try again") or 429 becomes a message instead.
//
// ParseThing has the same field rules as the Seeder's emit step (EmitCatalog); keep the two in step.
public class BggClient
{
    public const string TokenSetting = "Bgg:Token";

    private const string ApiUrl = "https://boardgamegeek.com/xmlapi2/";

    // The most matches the choice list shows. "Catan" alone has hundreds.
    public const int MaxMatches = 20;

    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<BggClient> _logger;

    public BggClient(IConfiguration config, ILogger<BggClient> logger)
    {
        _config = config;
        _logger = logger;

        // One HttpClient for the app (this class is a singleton), so connections are reused.
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config[TokenSetting]);

    // Board games (not expansions) whose name matches, the exact name first.
    public async Task<List<BggMatch>> SearchAsync(string name)
    {
        string xml = await GetAsync("search?type=boardgame&query=" + Uri.EscapeDataString(name.Trim()));
        try
        {
            return ParseSearch(xml, name);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new BggLookupException("BoardGameGeek's answer couldn't be read.", ex);
        }
    }

    // The Game's facts, ready to add to the Catalog.
    public async Task<Game> FetchGameAsync(int bggId)
    {
        string xml = await GetAsync("thing?stats=1&id=" + bggId.ToString(CultureInfo.InvariantCulture));
        try
        {
            return ParseThing(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new BggLookupException("BoardGameGeek's answer couldn't be read.", ex);
        }
    }

    // The search answer: <items><item type="boardgame" id="13"><name type="primary" value="CATAN"/>
    // <yearpublished value="1995"/></item>...</items>. The name may be an alternate one (another
    // language, an old title); the Game's real name comes from /thing.
    public static List<BggMatch> ParseSearch(string xml, string searchedFor)
    {
        List<BggMatch> exact = [];
        List<BggMatch> others = [];
        HashSet<int> seen = [];

        XElement root = XDocument.Parse(xml).Root!;
        foreach (XElement item in root.Elements("item"))
        {
            // The search can return expansions too; they aren't Games you sit down and play.
            string? type = (string?)item.Attribute("type");
            if (type != null && type != "boardgame")
            {
                continue;
            }

            int id;
            if (!int.TryParse((string?)item.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                continue;
            }

            // BGG can list one game twice (under two names); show it once.
            if (!seen.Add(id))
            {
                continue;
            }

            string? matchName = (string?)item.Element("name")?.Attribute("value");
            if (string.IsNullOrWhiteSpace(matchName))
            {
                continue;
            }

            int? year = null;
            int parsedYear;
            if (int.TryParse((string?)item.Element("yearpublished")?.Attribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedYear))
            {
                year = parsedYear;
            }

            BggMatch match = new BggMatch { Id = id, Name = matchName, Year = year };
            if (string.Equals(match.Name, searchedFor.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(match);
            }
            else
            {
                others.Add(match);
            }
        }

        List<BggMatch> matches = [];
        matches.AddRange(exact);
        matches.AddRange(others);
        return matches;
    }

    // The /thing answer with stats, read with the same rules as the Seeder's emit step.
    public static Game ParseThing(string xml)
    {
        XElement? item = XDocument.Parse(xml).Root?.Element("item");
        if (item == null)
        {
            throw new BggLookupException("BoardGameGeek has no game with that id.");
        }

        XElement? primaryName = null;
        foreach (XElement nameElement in item.Elements("name"))
        {
            if ((string?)nameElement.Attribute("type") == "primary")
            {
                primaryName = nameElement;
                break;
            }
        }

        string? name = (string?)primaryName?.Attribute("value");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BggLookupException("BoardGameGeek's entry has no name.");
        }

        string designer = "Unknown";
        foreach (XElement link in item.Elements("link"))
        {
            if ((string?)link.Attribute("type") == "boardgamedesigner")
            {
                designer = (string?)link.Attribute("value") ?? "Unknown";
                break;
            }
        }

        int minPlayers = ReadNumber(item, "minplayers");
        int maxPlayers = ReadNumber(item, "maxplayers");
        int playtimeMinutes = ReadNumber(item, "playingtime");

        // emit drops these as a data flaw; here the owner is told instead. new Play would refuse every
        // Play of a Game that seats nobody.
        if (maxPlayers < 1)
        {
            throw new BggLookupException(name + " has no player count on BoardGameGeek, so it can't be added.");
        }

        // Absent on some items, "0" on others. Both mean nobody rated it, not that it is light.
        double? weight = null;
        string? weightText = (string?)item.Element("statistics")?.Element("ratings")?.Element("averageweight")?.Attribute("value");
        double parsedWeight;
        if (double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedWeight) && parsedWeight > 0)
        {
            weight = parsedWeight;
        }

        // XDocument already undid the XML layer (&#10;, &amp;). What is left is HTML (&mdash;, &quot;).
        string blurb = WebUtility.HtmlDecode(item.Element("description")?.Value ?? "").Trim();

        return new Game
        {
            Name = name,
            Designer = designer,
            MinPlayers = minPlayers,
            MaxPlayers = maxPlayers,
            PlaytimeMinutes = playtimeMinutes,
            Categories = LinkValues(item, "boardgamecategory"),
            Mechanics = LinkValues(item, "boardgamemechanic"),
            Weight = weight,
            Blurb = blurb.Length > 0 ? blurb : null,
        };
    }

    // One call, one try. Every way it can fail ends as a BggLookupException with a message to show.
    private async Task<string> GetAsync(string pathAndQuery)
    {
        string? token = _config[TokenSetting];
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new BggLookupException("BoardGameGeek lookup isn't set up: there is no " + TokenSetting + " setting.");
        }

        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ApiUrl + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (TaskCanceledException ex)
        {
            throw new BggLookupException("BoardGameGeek took too long to answer. Try again in a minute.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not reach BoardGameGeek.");
            throw new BggLookupException("Couldn't reach BoardGameGeek. Try again in a minute.", ex);
        }

        using (response)
        {
            int status = (int)response.StatusCode;

            // 202: BGG has queued the request and wants it asked again later.
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                throw new BggLookupException("BoardGameGeek is still getting that ready. Try again in a few seconds.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
            {
                throw new BggLookupException("BoardGameGeek is busy (" + status + "). Wait a minute and try again.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("BoardGameGeek refused the token: {Status}.", status);
                throw new BggLookupException("BoardGameGeek refused the lookup token (" + status + "). Check the " + TokenSetting + " setting.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new BggLookupException("BoardGameGeek answered " + status + ".");
            }

            try
            {
                return await response.Content.ReadAsStringAsync();
            }
            catch (TaskCanceledException ex)
            {
                throw new BggLookupException("BoardGameGeek took too long to answer. Try again in a minute.", ex);
            }
        }
    }

    // The value attribute of a number element such as <minplayers value="2"/>. Missing means 0, which
    // the Table reads as unknown.
    private static int ReadNumber(XElement item, string elementName)
    {
        int number;
        if (int.TryParse((string?)item.Element(elementName)?.Attribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private static List<string> LinkValues(XElement item, string type)
    {
        List<string> values = [];
        foreach (XElement link in item.Elements("link"))
        {
            if ((string?)link.Attribute("type") == type)
            {
                string? value = (string?)link.Attribute("value");
                if (value != null)
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }
}
