-- A-12: the games table for the PostgreSQL Search Index, and its indexes.
--
-- Run by the Seeder, not by hand:  dotnet run --project MeepleLedger.Seeder -- upload-postgres
-- Every statement is IF NOT EXISTS, so running it again changes nothing.
--
-- One row per Game in the Catalog, keyed by name like the Cosmos games container (whose id is the
-- name). The column types are chosen on purpose:
--   name, designer      text       Postgres has no speed or size advantage for varchar(n), and a
--                                  length limit would only be a guess at BGG's longest title.
--   players, playtime   integer    0 means "unknown" in BGG data, exactly as in Game and the Cosmos
--                                  documents, so it is kept as 0 rather than turned into NULL. That
--                                  keeps the A-13 WHERE clause a mirror of Table.Admits.
--   categories,         text[]     A Game has a short list of each. An array keeps them on the row
--   mechanics                      (no join tables) and A-13 can GIN-index them for && and @>.
--   weight              real       float4 is plenty for a 1-5 average. NULL means unrated, not
--                                  light (the same rule as Game.Weight), so no NOT NULL.
--   blurb               text       Long and optional. NULL until the seeder has written one.
--   embedding           vector     The pgvector type. Storing it as float8[] or json would load
--                       (1536)     fine and cost the vector index entirely. 1536 matches the
--                                  game-embeddings deployment and the Cosmos vector policy.
--   embedding_text_hash text       The data/vectors.json hash of the text the vector came from,
--                                  as in the Cosmos documents (A-10).

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS games (
    name                text        PRIMARY KEY,
    designer            text        NOT NULL,
    min_players         integer     NOT NULL CHECK (min_players >= 0),
    max_players         integer     NOT NULL CHECK (max_players >= 0),
    playtime_minutes    integer     NOT NULL CHECK (playtime_minutes >= 0),
    categories          text[]      NOT NULL DEFAULT '{}',
    mechanics           text[]      NOT NULL DEFAULT '{}',
    weight              real        CHECK (weight > 0),
    blurb               text,
    embedding           vector(1536) NOT NULL,
    embedding_text_hash text        NOT NULL
);

-- B-tree indexes on the columns the Table filters on (player count and minutes available).
-- The player count is checked against both ends of a range, so one two-column index covers it.
-- At 200 rows the planner will usually ignore both and scan the table, which is the right call at
-- that size; they are here for the shape of the query, not for today's row count.
CREATE INDEX IF NOT EXISTS games_players ON games (min_players, max_players);
CREATE INDEX IF NOT EXISTS games_playtime ON games (playtime_minutes);

-- The vector index: HNSW, chosen over IVFFlat in A-12 (the measurements and the reason are in the
-- ticket). The operator class must match the query's operator: vector_cosine_ops serves <=>
-- (cosine distance, the same distance function as the Cosmos vector policy). A query using <->
-- (L2) would ignore this index and silently scan the table.
--
-- Unlike IVFFlat, HNSW does not learn anything from the rows present when it is built, so creating
-- it before the rows are loaded is safe: each insert is added to the graph as it arrives.
-- m and ef_construction are build-time settings (written out here at pgvector's defaults);
-- hnsw.ef_search is the query-time recall dial.
CREATE INDEX IF NOT EXISTS games_embedding_hnsw ON games
    USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
