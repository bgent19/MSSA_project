# MeepleLedger — domain language

The vocabulary this project commits to. Terms here are used exactly as defined, in code, in
tickets and in conversation. This file is a glossary and nothing else — no implementation
details, no decisions. Decisions live in [docs/adr/](docs/adr/).

## The collection

**Game** — a board game as it exists in the world, independent of whether anyone owns it. Has a
name, a designer, a player-count range and a playtime. A Game is never "yours"; it is a fact about
the hobby.

**Blurb** — the prose description of a Game, as written on BoardGameGeek. Distinct from the Game's
structured facts because it is the only free text the project holds, and therefore the only thing
that can carry meaning a number cannot. A Game may have no Blurb.

**Catalog** — every Game the app knows about. Searchable, never owned. The Catalog is the universe;
the Collection is a subset of it.

**Owned Game** — a Game that is on your shelf, together with the facts that only apply because it is
yours: when you acquired it, what condition it is in, and any notes. The distinction between Game
and Owned Game is the spine of the model — a Play can reference a Game you do not own, and the app
says so out loud.

**Collection** — the set of Owned Games. Refuses duplicates.

**Condition** — the shelf-wear of an Owned Game: Mint, Good, Played, Worn. A property of your copy,
never of the Game.

## The history

**Play** — one session of one Game on one date, with the people who were there and how it went.

**Player Result** — one person's outcome within a Play: who they were and whether they won.

**Play Log** — the full history of Plays. Answers questions about the past; it does not own the
Collection and does not care whether a played Game is owned.

## Recommendation

**Table** — the constraints of a specific game night: how many people are at the table, how many
minutes are available, and whether the answer must come from the Collection or may come from the
whole Catalog. "Table" is the domain's word for what a search engine would call a filter — the
project prefers the board-game word, because the constraints are physical facts about a room, not
query syntax.

**Ask** — the natural-language sentence a person types when they want something to play. "Something
co-operative and quick for three." An Ask carries intent; a Table carries constraints. They are
separate because a Table can be inferred from who is present, while an Ask can only be typed.

**Recommendation** — a Game proposed in answer to an Ask, with its rank and the reason it was
chosen. A Recommendation is always explainable: if it cannot say why, it is a guess, not a
Recommendation.

**Search Index** — the thing that turns an Ask plus a Table into ranked Games. Deliberately named
for what it does rather than what it is built on, because the project has several and they are
interchangeable.

**Provider** — which concrete implementation is currently serving the Search Index and the store.
A deployment-time fact, never a domain one. The domain must never be able to tell which Provider is
live.
