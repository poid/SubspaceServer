-- PowerBall league database schema (PostgreSQL).
--
-- Ported from the ASSS pbmod MySQL schema (EVENTS/SIGNUPS/TEAMS/TEAMS_PLAYERS in signups.h / teams.h).
-- The PowerBallDatabase module applies this automatically on load (CREATE ... IF NOT EXISTS), so running it
-- by hand is optional. Point the server at the database via global.conf:
--
--   [ SS.PowerBall ]
--   DatabaseConnectionString = Host=localhost;Username=...;Password=...;Database=...
--
-- Case handling: 'name' stores the value exactly as the player typed it (for display, e.g. on a stats website),
-- and 'name_key' stores the upper-cased form used for case-insensitive matching / uniqueness / lookups.

CREATE SCHEMA IF NOT EXISTS pb;

-- Named events that players can sign up for.
CREATE TABLE IF NOT EXISTS pb.event (
    id          serial       PRIMARY KEY,
    name        varchar(32)  NOT NULL,           -- as entered (display)
    name_key    varchar(32)  NOT NULL UNIQUE,    -- upper-cased (matching)
    description varchar(250) NOT NULL DEFAULT '',
    active      boolean      NOT NULL DEFAULT false
);

-- A player's sign-up for an event.
CREATE TABLE IF NOT EXISTS pb.signup (
    event_id int         NOT NULL REFERENCES pb.event(id) ON DELETE CASCADE,
    name     varchar(32) NOT NULL,               -- as entered (display)
    name_key varchar(32) NOT NULL,               -- upper-cased (matching)
    PRIMARY KEY (event_id, name_key)
);

-- A saved team roster.
CREATE TABLE IF NOT EXISTS pb.team (
    id       serial      PRIMARY KEY,
    name     varchar(64) NOT NULL,               -- as entered (display)
    name_key varchar(64) NOT NULL UNIQUE,        -- upper-cased (matching)
    captain  varchar(32) NOT NULL DEFAULT ''     -- as entered (display)
);

-- A player on a saved team.
CREATE TABLE IF NOT EXISTS pb.team_player (
    team_id  int         NOT NULL REFERENCES pb.team(id) ON DELETE CASCADE,
    name     varchar(32) NOT NULL,               -- as entered (display)
    name_key varchar(32) NOT NULL,               -- upper-cased (matching)
    PRIMARY KEY (team_id, name_key)
);
