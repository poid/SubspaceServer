using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.PowerBall.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SS.PowerBall.Modules
{
    public sealed partial class Teams
    {
        #region Sign-up integration

        private void RepopulateSignup(string eventName, string playerName)
        {
            RunDb(() => _signUps.AddPlayerAsync(eventName, playerName));
        }

        private void RemoveFromSignup(string eventName, string playerName)
        {
            RunDb(() => _signUps.RemovePlayerAsync(eventName, playerName));
        }

        private void UsingSignupList(Arena arena, ArenaData ad, Player picker, Team team, string targetName, bool captainPath)
        {
            string ev = ad.ActiveEvent!;
            RunGuarded(async () =>
            {
                SignUpMatch match = await _signUps.IsPlayerSignedUpAsync(ev, targetName); // resumes on the mainloop

                switch (match.Count)
                {
                    case -1:
                        return; // error (already reported / silent)

                    case 0:
                        _chat.SendMessage(picker, $"{targetName} is not available. Check ?listsignups for available players.");
                        return;

                    case 1:
                        // Re-validate after the await: the arena may have recycled, the event/team may have changed,
                        // and (crucially) an earlier ?pick for the same turn may have already advanced the pick — so a
                        // spammed second pick doesn't slip a second player onto one turn.
                        if (!arena.TryGetExtraData(_adKey, out ArenaData? liveAd)
                            || liveAd.ActiveEvent != ev
                            || !liveAd.Teams.Contains(team))
                            return;

                        if (captainPath
                            && liveAd.PickingStage is not (PickingStage.GameStart or PickingStage.Completed)
                            && !IsCurrentPick(liveAd, team.Frequency))
                            return;

                        string name = match.Names[0];
                        Player? online = _playerData.FindPlayer(name);
                        if (online is null && (!liveAd.IsDraft || !liveAd.OfflineDrafting))
                        {
                            _chat.SendMessage(picker, $"Player {name} is not online so cannot be picked.");
                            return;
                        }

                        AddPlayer(arena, liveAd, team, online, name, picker); // online may be null for an offline draft
                        return;

                    default:
                        _chat.SendMessage(picker, $"Found {match.Count} matches on the signup list for {targetName}.");
                        return;
                }
            });
        }

        private void Cmd_UseSignUps(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            if (!_signUps.IsAvailable)
            {
                _chat.SendMessage(player, "Cannot access signup module thus cannot use signups.");
                return;
            }

            if (args.IsEmpty)
            {
                _chat.SendMessage(player, $"Active event: {ad.ActiveEvent ?? "(none)"}");
                return;
            }

            if (args.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                ad.ActiveEvent = null;
                _chat.SendMessage(player, "Using event signup list disabled.");
                return;
            }

            string ev = args.ToString();
            RunGuarded(async () =>
            {
                if (!await _signUps.IsValidEventAsync(ev))
                {
                    _chat.SendMessage(player, $"{ev} is not a valid sign up event.");
                    return;
                }

                // Re-fetch after the await in case the arena recycled during the DB round-trip.
                if (!arena.TryGetExtraData(_adKey, out ArenaData? liveAd))
                    return;

                liveAd.ActiveEvent = ev;
                _chat.SendMessage(player, $"Active event set to {ev}");
            });
        }

        #endregion

        #region Saved-team commands

        private void Cmd_SaveTeams(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            // No argument: just report the current status (no DB required).
            if (args.IsEmpty)
            {
                _chat.SendMessage(player, $"Save Teams currently set as {(ad.SaveTeams ? "ON" : "OFF")}");
                return;
            }

            if (args.StartsWith("ON", StringComparison.OrdinalIgnoreCase))
            {
                if (ad.SaveTeams)
                {
                    _chat.SendMessage(player, "Save teams is already set to ON");
                    return;
                }
                if (!_db.IsAvailable)
                {
                    ad.SaveTeams = false;
                    _chat.SendMessage(player, "Database connection not available. Save teams currently set as OFF");
                    return;
                }

                List<TeamSnapshot> snapshot = SnapshotTeams(ad);
                RunGuarded(async () =>
                {
                    List<string> teamNames = new(snapshot.Count);
                    foreach (TeamSnapshot t in snapshot)
                        teamNames.Add(t.Name);

                    IReadOnlyList<string> existing = await _db.CheckExistingTeamsAsync(teamNames);
                    if (existing.Count > 0)
                    {
                        int i = 0;
                        foreach (string e in existing)
                            _chat.SendMessage(player, $"{++i,2}.{e} already exists.");
                        _chat.SendMessage(player, "Cannot save teams until the new team names are unique.");
                        return;
                    }

                    // Re-fetch after the await in case the arena recycled during the existence check.
                    if (!arena.TryGetExtraData(_adKey, out ArenaData? liveAd))
                        return;

                    liveAd.SaveTeams = true;
                    _chat.SendMessage(player, "Save Teams currently set as ON and current teams saved.");

                    // Write the snapshot through the serialized DB chain so it commits ahead of any later roster
                    // mutations (each team's row before its players).
                    foreach (TeamSnapshot t in snapshot)
                    {
                        TeamSnapshot captured = t;
                        RunDb(async () =>
                        {
                            await _db.AddTeamAsync(captured.Name, captured.Captain);
                            foreach (string p in captured.Players)
                                await _db.AddTeamPlayerAsync(captured.Name, p);
                        });
                    }
                });
                return;
            }

            if (args.StartsWith("OFF", StringComparison.OrdinalIgnoreCase))
            {
                if (!ad.SaveTeams)
                {
                    _chat.SendMessage(player, "Save teams is already set to OFF");
                    return;
                }
                if (!_db.IsAvailable)
                {
                    ad.SaveTeams = false;
                    _chat.SendMessage(player, "Save Teams currently set as OFF");
                    return;
                }

                List<string> names = SnapshotTeamNames(ad);
                ad.SaveTeams = false;
                RunGuarded(async () =>
                {
                    foreach (string name in names)
                        await _db.DeleteTeamAsync(name);
                    _chat.SendMessage(player, "Save Teams currently set as OFF");
                });
                return;
            }

            _chat.SendMessage(player, "Please specify ON or OFF.");
        }

        private void Cmd_SavedTeams(Player player)
        {
            if (!CheckDb(player))
                return;

            RunGuarded(async () =>
            {
                IReadOnlyList<SavedTeamInfo> teams = await _db.GetTeamsAsync();
                if (teams.Count == 0)
                {
                    _chat.SendMessage(player, "No teams currently saved.");
                    return;
                }

                int i = 0;
                foreach (SavedTeamInfo t in teams)
                    _chat.SendMessage(player, $"{++i,2}.{t.Name,-32} [{t.Captain}]");
            });
        }

        private void Cmd_ListSavedTeam(Player player, ReadOnlySpan<char> args)
        {
            if (!CheckDb(player))
                return;

            if (args.IsEmpty)
            {
                _chat.SendMessage(player, "You must specify a team to list the players for.");
                return;
            }

            string name = args.Trim().ToString();
            RunGuarded(async () =>
            {
                IReadOnlyList<SavedTeamInfo> matches = await _db.FindTeamsAsync(name);
                if (matches.Count == 0)
                {
                    _chat.SendMessage(player, $"No teams found matching to {name}");
                    return;
                }

                // List every fuzzy match, each with its roster.
                foreach (SavedTeamInfo t in matches)
                {
                    _chat.SendMessage(player, $"Team {t.Name} - Captain: {t.Captain}");

                    IReadOnlyList<string> players = await _db.GetTeamPlayersAsync(t.Name);
                    if (players.Count == 0)
                    {
                        _chat.SendMessage(player, "No players found for team.");
                        continue;
                    }

                    foreach (string p in players)
                        _chat.SendMessage(player, $"+ {p}");
                }
            });
        }

        private void Cmd_DelSavedTeam(Player player, ReadOnlySpan<char> args)
        {
            if (!CheckDb(player))
                return;

            string name = args.Trim().ToString();
            RunGuarded(async () =>
                _chat.SendMessage(player, await _db.DeleteTeamAsync(name) ? "Team deleted!" : "Unable to delete team."));
        }

        private void Cmd_LoadTeam(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            if (!CheckDb(player))
                return;

            if (args.IsEmpty)
            {
                _chat.SendMessage(player, "You must specify a team name and frequency.");
                return;
            }
            if (!args.Contains(':'))
            {
                _chat.SendMessage(player, "You must specify a team name and frequency. ie ?loadteam 100:Best Team");
                return;
            }

            SplitColon(args, out ReadOnlySpan<char> freqSpan, out ReadOnlySpan<char> nameSpan);
            ReadOnlySpan<char> freqTrimmed = freqSpan.Trim();
            if (!int.TryParse(freqTrimmed, out int freq) || freq < 0 || (freq == 0 && freqTrimmed[0] != '0'))
            {
                _chat.SendMessage(player, "No frequency specified");
                return;
            }

            string name = nameSpan.Trim().ToString();
            RunGuarded(async () =>
            {
                IReadOnlyList<SavedTeamInfo> matches = await _db.FindTeamsAsync(name);
                if (matches.Count == 0)
                {
                    _chat.SendMessage(player, $"No teams found matching to {name}");
                    return;
                }

                if (matches.Count > 1 && !HasExact(matches, name))
                {
                    _chat.SendMessage(player, $"Found {matches.Count} teams matching to {name}");
                    foreach (SavedTeamInfo t in matches)
                        _chat.SendMessage(player, $"  {t.Name}");
                    return;
                }

                SavedTeamInfo chosen = PickExactOrFirst(matches, name);

                // Re-fetch after the first DB round-trip in case the arena recycled during it.
                if (!arena.TryGetExtraData(_adKey, out ArenaData? liveAd))
                    return;

                Team? team = AddTeam(arena, liveAd, null, freq, chosen.Name, isLoaded: true);
                if (team is null)
                {
                    _chat.SendMessage(player, $"Could not load team {chosen.Name} onto freq {freq}.");
                    return;
                }

                team.Captain = chosen.Captain;

                IReadOnlyList<string> players = await _db.GetTeamPlayersAsync(chosen.Name);

                // Re-validate after the second round-trip: the arena may have recycled, or the team we just added may
                // have been removed by a concurrent ?newteams/?removeteam.
                if (!arena.TryGetExtraData(_adKey, out liveAd) || !liveAd.Teams.Contains(team))
                    return;

                foreach (string p in players)
                {
                    AddPlayerToList(arena, liveAd, team, p, SpecShip, wasLoaded: true, wasBorrowed: false);

                    Player? online = _playerData.FindPlayer(p);
                    if (online is not null && online.Arena == arena)
                    {
                        _game.SetShipAndFreq(online, ShipType.Spec, (short)freq);
                        _chat.SendMessage(player, $"Added {p}");
                    }
                    else
                    {
                        _chat.SendMessage(player, $"{p} not available in arena.");
                    }
                }

                team.PickedCount = 0; // loaded teams are counted via PlayersInGame
                _chat.SendArenaMessage(arena, $"Team {chosen.Name} loaded onto frequency {freq}.");
            });
        }

        #endregion

        #region Helpers

        private bool CheckDb(Player player)
        {
            if (_db.IsAvailable)
                return true;

            _chat.SendMessage(player, "Unfortunately the database is currently not available. Cannot process request.");
            return false;
        }

        private static List<string> SnapshotTeamNames(ArenaData ad)
        {
            List<string> names = new(ad.Teams.Count);
            foreach (Team team in ad.Teams)
                names.Add(team.TeamName);
            return names;
        }

        private static List<TeamSnapshot> SnapshotTeams(ArenaData ad)
        {
            List<TeamSnapshot> list = new(ad.Teams.Count);
            foreach (Team team in ad.Teams)
            {
                List<string> players = new(team.Players.Count);
                foreach (TeamPlayer tp in team.Players)
                    players.Add(tp.Name);

                list.Add(new TeamSnapshot(team.TeamName, team.Captain ?? "", players));
            }

            return list;
        }

        private static bool HasExact(IReadOnlyList<SavedTeamInfo> teams, string name)
        {
            foreach (SavedTeamInfo t in teams)
            {
                if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static SavedTeamInfo PickExactOrFirst(IReadOnlyList<SavedTeamInfo> teams, string name)
        {
            foreach (SavedTeamInfo t in teams)
            {
                if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return t;
            }

            return teams[0];
        }

        private Task _dbWriteChain = Task.CompletedTask;
        private readonly object _dbWriteChainLock = new();

        private void RunDb(Func<Task> action)
        {
            // Serialize DB writes so dependent writes (a team row, then its captain/players) commit in the order they
            // were submitted on the mainloop, instead of racing on separate connections — otherwise a child row can be
            // written before its parent commits and silently insert 0 rows.
            lock (_dbWriteChainLock)
            {
                _dbWriteChain = _dbWriteChain.ContinueWith(_ => RunGuardedAsync(action), TaskScheduler.Default).Unwrap();
            }
        }

        private void RunGuarded(Func<Task> action)
        {
            _ = RunGuardedAsync(action);
        }

        private async Task RunGuardedAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Teams), $"League database error. {ex}");
            }
        }

        private readonly record struct TeamSnapshot(string Name, string Captain, List<string> Players);

        #endregion
    }
}
