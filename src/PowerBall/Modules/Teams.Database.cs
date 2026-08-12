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

        private void UsingSignupList(Arena arena, ArenaData ad, Player picker, Team team, string targetName)
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
                        string name = match.Names[0];
                        Player? online = _playerData.FindPlayer(name);
                        if (online is null && (!ad.IsDraft || !ad.OfflineDrafting))
                        {
                            _chat.SendMessage(picker, $"Player {name} is not online so cannot be picked.");
                            return;
                        }

                        AddPlayer(arena, ad, team, online, name); // online may be null for an offline draft
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

                ad.ActiveEvent = ev;
                _chat.SendMessage(player, $"Active event set to {ev}");
            });
        }

        #endregion

        #region Saved-team commands

        private void Cmd_SaveTeams(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            if (!_db.IsAvailable)
            {
                _chat.SendMessage(player, "No DB connection available.");
                return;
            }

            if (args.StartsWith("OFF", StringComparison.OrdinalIgnoreCase))
            {
                List<string> names = SnapshotTeamNames(ad);
                ad.SaveTeams = false;
                RunGuarded(async () =>
                {
                    foreach (string name in names)
                        await _db.DeleteTeamAsync(name);
                    _chat.SendMessage(player, "Team saving canceled and saved teams removed.");
                });
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
                    return;
                }

                ad.SaveTeams = true;
                _chat.SendMessage(player, "Save teams enabled and current teams saved.");

                foreach (TeamSnapshot t in snapshot)
                {
                    await _db.AddTeamAsync(t.Name, t.Captain);
                    foreach (string p in t.Players)
                        await _db.AddTeamPlayerAsync(t.Name, p);
                }
            });
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

            string name = args.Trim().ToString();
            RunGuarded(async () =>
            {
                IReadOnlyList<SavedTeamInfo> matches = await _db.FindTeamsAsync(name);
                if (matches.Count == 0)
                {
                    _chat.SendMessage(player, $"No teams found matching to {name}");
                    return;
                }

                SavedTeamInfo chosen = PickExactOrFirst(matches, name);
                _chat.SendMessage(player, $"Team {chosen.Name} - Captain: {chosen.Captain}");

                IReadOnlyList<string> players = await _db.GetTeamPlayersAsync(chosen.Name);
                foreach (string p in players)
                    _chat.SendMessage(player, $"+ {p}");
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

            SplitColon(args, out ReadOnlySpan<char> freqSpan, out ReadOnlySpan<char> nameSpan);
            if (!int.TryParse(freqSpan.Trim(), out int freq))
            {
                _chat.SendMessage(player, "Usage: ?loadteam <freq>:<name>");
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

                Team? team = AddTeam(arena, ad, null, freq, chosen.Name, isLoaded: true);
                if (team is null)
                {
                    _chat.SendMessage(player, $"Could not load team {chosen.Name} onto freq {freq}.");
                    return;
                }

                team.Captain = chosen.Captain;

                IReadOnlyList<string> players = await _db.GetTeamPlayersAsync(chosen.Name);
                foreach (string p in players)
                {
                    AddPlayerToList(arena, ad, team, p, SpecShip, wasLoaded: true, wasBorrowed: false);

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

        private void RunDb(Func<Task> action)
        {
            _ = RunGuardedAsync(action);
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
                _logManager.LogM(LogLevel.Error, nameof(Teams), $"League database error. {ex.Message}");
            }
        }

        private readonly record struct TeamSnapshot(string Name, string Captain, List<string> Players);

        #endregion
    }
}
