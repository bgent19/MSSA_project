using MeepleLedger.Domain;
using MeepleLedger.Storage;

namespace MeepleLedger.Tests
{
    // S-07: reading a Game from BoardGameGeek's XML, the text it is embedded from, and an added Game
    // surviving the export and import round trip.
    public class AddedGameTests
    {
        private const string ThingXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <items termsofuse="https://boardgamegeek.com/xmlapi/termsofuse">
              <item type="boardgame" id="266192">
                <name type="primary" sortindex="1" value="Wingspan" />
                <name type="alternate" sortindex="1" value="Flügelschlag" />
                <description>Attract a beautiful &amp;mdash; and diverse &amp;mdash; collection of birds.&#10;&#10;  </description>
                <minplayers value="1" />
                <maxplayers value="5" />
                <playingtime value="70" />
                <link type="boardgamecategory" id="1089" value="Animals" />
                <link type="boardgamecategory" id="1002" value="Card Game" />
                <link type="boardgamemechanic" id="2041" value="Card Drafting" />
                <link type="boardgamemechanic" id="2004" value="Set Collection" />
                <link type="boardgamedesigner" id="116668" value="Elizabeth Hargrave" />
                <statistics page="1">
                  <ratings>
                    <averageweight value="2.4612" />
                  </ratings>
                </statistics>
              </item>
            </items>
            """;

        [Fact]
        public void ParseThing_ReadsTheFactsWithTheSeedersRules()
        {
            Game game = BggClient.ParseThing(ThingXml);

            Assert.Equal("Wingspan", game.Name);
            Assert.Equal("Elizabeth Hargrave", game.Designer);
            Assert.Equal(1, game.MinPlayers);
            Assert.Equal(5, game.MaxPlayers);
            Assert.Equal(70, game.PlaytimeMinutes);
            Assert.Equal(["Animals", "Card Game"], game.Categories);
            Assert.Equal(["Card Drafting", "Set Collection"], game.Mechanics);
            Assert.Equal(2.4612, game.Weight);

            // XML decoded by XDocument, then the HTML layer decoded, then trimmed.
            Assert.Equal("Attract a beautiful — and diverse — collection of birds.", game.Blurb);
        }

        [Theory]
        [InlineData("<averageweight value=\"0\" />")]
        [InlineData("")]
        public void ParseThing_ReadsAZeroOrMissingWeightAsUnrated(string weightElement)
        {
            string xml = ThingXml.Replace("<averageweight value=\"2.4612\" />", weightElement);

            Game game = BggClient.ParseThing(xml);

            Assert.Null(game.Weight);
        }

        [Fact]
        public void ParseThing_RefusesAGameThatSeatsNobody()
        {
            string xml = ThingXml.Replace("<maxplayers value=\"5\" />", "<maxplayers value=\"0\" />");

            Assert.Throws<BggLookupException>(() => BggClient.ParseThing(xml));
        }

        [Fact]
        public void ParseSearch_PutsTheExactNameFirstAndSkipsExpansionsAndRepeats()
        {
            const string xml = """
                <items total="4">
                  <item type="boardgame" id="1"><name type="primary" value="Catan: Seafarers" /><yearpublished value="1997" /></item>
                  <item type="boardgameexpansion" id="2"><name type="primary" value="Catan: Cities" /></item>
                  <item type="boardgame" id="13"><name type="primary" value="CATAN" /><yearpublished value="1995" /></item>
                  <item type="boardgame" id="13"><name type="alternate" value="Settlers of Catan" /></item>
                </items>
                """;

            List<BggMatch> matches = BggClient.ParseSearch(xml, "catan");

            Assert.Equal(2, matches.Count);
            Assert.Equal(13, matches[0].Id);
            Assert.Equal(1995, matches[0].Year);
            Assert.Equal("Catan: Seafarers", matches[1].Name);
        }

        [Fact]
        public void EmbeddingText_MatchesTheSeedersTextExactly()
        {
            // The Seeder's BuildEmbeddingText, character for character: "\r\n" line ends, a blank line,
            // then the Blurb. A difference here means watch-cosmos re-queues every added Game.
            string text = EmbeddingText.Build("Wingspan", ["Animals", "Card Game"], ["Card Drafting"], "Birds.");

            Assert.Equal("Name: Wingspan\r\nCategories: Animals, Card Game\r\nMechanics: Card Drafting\r\n\r\nBirds.", text);
            Assert.Equal(64, EmbeddingText.Hash(text).Length);
            Assert.Equal(EmbeddingText.Hash(text).ToUpperInvariant(), EmbeddingText.Hash(text));
        }

        [Fact]
        public void Export_CarriesAnAddedGameAndImportRestoresItFirst()
        {
            Game catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };
            List<Game> compiled = [catan];
            GameCatalog catalog = new GameCatalog(compiled);

            Game wingspan = BggClient.ParseThing(ThingXml);
            catalog.Add(wingspan);

            GameCollection collection = new GameCollection();
            collection.Add(new OwnedGame { Game = wingspan, Condition = Condition.Mint, DateAcquired = new DateTime(2026, 10, 1) });

            PlayLog playLog = new PlayLog { OwnerName = "TheGentleBean" };
            playLog.Record(new Play(wingspan, new DateTime(2026, 10, 2), [new PlayerResult { PlayerName = "TheGentleBean", IsWinner = true }]));
            playLog.Record(new Play(catan, new DateTime(2026, 10, 3), [new PlayerResult { PlayerName = "TheGentleBean" }]));

            LedgerExportFile file = LedgerExport.Build(collection, playLog, compiled, new DateTime(2026, 10, 29));
            string json = LedgerExport.ToJson(file);

            // Only the Game outside the compiled Catalog is carried, with its facts and Blurb.
            Assert.Single(file.Games);
            Assert.Equal("Wingspan", file.Games[0].Name);
            Assert.Equal(wingspan.Blurb, file.Games[0].Blurb);

            LoadedLedger loaded = LedgerExport.Load(LedgerExport.FromJson(json), compiled);

            Assert.NotNull(loaded.Catalog.Find("Wingspan"));
            Assert.True(loaded.Collection.Owns("Wingspan"));
            Assert.Equal(2, loaded.PlayLog.RecentFirst().Count());

            // Exporting what was imported gives the same bytes.
            LedgerExportFile again = LedgerExport.Build(loaded.Collection, loaded.PlayLog, compiled, new DateTime(2026, 10, 29));
            Assert.Equal(json, LedgerExport.ToJson(again));
        }
    }
}
