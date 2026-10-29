using Azure;
using Azure.Messaging.EventGrid;
using MeepleLedger.Domain;
using MeepleLedger.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeepleLedger.Tests
{
    // A-20: PlayLog.Recorded is what the PlayLogged publisher listens to, and publishing must never
    // stop a Play from being logged.
    public class PlayLoggedTests
    {
        private static readonly Game Catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };

        [Fact]
        public void PlayLog_Record_RaisesRecordedWithThePlay()
        {
            var log = new PlayLog { OwnerName = "Brett" };
            var raised = new List<Play>();
            log.Recorded += play => raised.Add(play);

            var play = new Play(Catan, DateTime.Today, [new PlayerResult { PlayerName = "Brett" }]);
            log.Record(play);

            Assert.Same(play, Assert.Single(raised));
        }

        [Fact]
        public void PlayLog_Record_DoesNotRaiseRecordedForARejectedPlay()
        {
            var log = new PlayLog { OwnerName = "Brett" };
            var raised = new List<Play>();
            log.Recorded += play => raised.Add(play);

            var withoutOwner = new Play(Catan, DateTime.Today, [new PlayerResult { PlayerName = "Alex" }]);

            Assert.Throws<InvalidOperationException>(() => log.Record(withoutOwner));
            Assert.Empty(raised);
        }

        [Fact]
        public void PlayLog_Replace_DoesNotRaiseRecorded()
        {
            var log = new PlayLog { OwnerName = "Brett" };
            var original = new Play(Catan, new DateTime(2026, 1, 1), [new PlayerResult { PlayerName = "Brett" }]);
            log.Record(original);

            var raised = new List<Play>();
            log.Recorded += play => raised.Add(play);

            var edited = new Play(Catan, new DateTime(2026, 1, 2), [new PlayerResult { PlayerName = "Brett" }]) { Id = original.Id };
            log.Replace(edited);

            Assert.Empty(raised);
        }

        [Fact]
        public void PlayLoggedPublisher_EventGridUnreachable_ThePlayIsStillLogged()
        {
            // Nothing listens on port 1, so every publish fails. Record must still return normally.
            var client = new EventGridPublisherClient(
                new Uri("https://127.0.0.1:1/api/events"),
                new AzureKeyCredential("not-a-real-key"));
            var store = new TestStore();
            var publisher = new PlayLoggedPublisher(client, store, NullLogger<PlayLoggedPublisher>.Instance);

            var play = new Play(Catan, DateTime.Today, [new PlayerResult { PlayerName = "Brett" }]);
            store.PlayLog.Record(play);

            Assert.Same(play, store.PlayLog.Get(play.Id));
        }

        private class TestStore : IMeepleStore
        {
            public GameCollection Collection { get; } = new();
            public PlayLog PlayLog { get; } = new() { OwnerName = "Brett" };

            public Task SaveOwnedGameAsync(OwnedGame ownedGame) => Task.CompletedTask;
            public Task RemoveOwnedGameAsync(string gameName) => Task.CompletedTask;
            public Task SavePlayAsync(Play play) => Task.CompletedTask;
            public Task RemovePlayAsync(Guid playId) => Task.CompletedTask;
        }
    }
}
