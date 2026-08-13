using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.PowerBall.ComponentInterfaces;
using System;
using System.Collections.Generic;

namespace SS.PowerBall.Modules
{
    public sealed partial class Teams
    {
        private static void SplitColon(ReadOnlySpan<char> input, out ReadOnlySpan<char> left, out ReadOnlySpan<char> right)
        {
            int colon = input.IndexOf(':');
            if (colon < 0)
            {
                left = input;
                right = default;
            }
            else
            {
                left = input[..colon];
                right = input[(colon + 1)..];
            }
        }

        #region Command registration

        private static readonly string[] CommandNames =
        [
            "teams", "caps", "captains", "currentpick", "pickstatus", "listborrows", "teamshelp", "teamsversion",
            "newteams", "addteam", "removeteam", "addcap", "addcaptain", "removecap", "removecaptain",
            "saveteams", "setteamship", "teammax", "teamingamemax", "draftmode", "offlinedrafting", "usesignups",
            "startpicking", "picktype", "nextpick", "freezeteams", "unfreezeteams", "setpickingstage", "sameteams",
            "pick", "add", "forceadd", "remove", "forceremove", "ready", "forceready", "sub", "forcesub",
            "borrow", "unborrow", "approve", "addborrow", "lagout", "forcelagout", "teamfreq", "forceteamfreq",
            "savedteams", "listsavedteams", "listsavedteam", "delsavedteam", "loadteam",
        ];

        private void AddCommands(Arena arena)
        {
            foreach (string name in CommandNames)
                _commandManager.AddCommand(name, Command_dispatch, arena);
        }

        private void RemoveCommands(Arena arena)
        {
            foreach (string name in CommandNames)
                _commandManager.RemoveCommand(name, Command_dispatch, arena);
        }

        [CommandHelp(Targets = CommandTarget.Any, Args = "varies", Description = "PowerBall league team commands. Use ?teamshelp for the list.")]
        private void Command_dispatch(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ReadOnlySpan<char> args = parameters.Trim();

            switch (commandName)
            {
                case "teams": Cmd_Teams(arena, ad, player); break;
                case "caps" or "captains": Cmd_Captains(arena, ad, player); break;
                case "currentpick": Cmd_CurrentPick(arena, ad, player); break;
                case "pickstatus": DisplayPickStatus(arena, ad, player); break;
                case "listborrows": Cmd_ListBorrows(arena, ad, player, args); break;
                case "teamshelp": PrintHelp(player); break;
                case "teamsversion": _chat.SendMessage(player, "PowerBall Teams module (C# port of ASSS Teams Module v1.1 by POiD)"); break;

                case "newteams": Cmd_NewTeams(arena, ad, player, args); break;
                case "addteam": Cmd_AddTeam(arena, ad, player, args); break;
                case "removeteam": Cmd_RemoveTeam(arena, ad, player, args); break;
                case "addcap" or "addcaptain": Cmd_AddCaptain(arena, ad, player, args, target); break;
                case "removecap" or "removecaptain": Cmd_RemoveCaptain(arena, ad, player, args, target); break;
                case "saveteams": Cmd_SaveTeams(arena, ad, player, args); break;
                case "setteamship": Cmd_SetTeamShip(arena, ad, player, args); break;
                case "teammax": Cmd_TeamMax(arena, ad, player, args, inGame: false); break;
                case "teamingamemax": Cmd_TeamMax(arena, ad, player, args, inGame: true); break;
                case "draftmode": Cmd_Toggle(arena, player, args, () => ad.IsDraft, v => ad.IsDraft = v, "Draft mode"); break;
                case "offlinedrafting": Cmd_Toggle(arena, player, args, () => ad.OfflineDrafting, v => ad.OfflineDrafting = v, "Offline drafting"); break;
                case "usesignups": Cmd_UseSignUps(arena, ad, player, args); break;

                case "startpicking": Cmd_StartPicking(arena, ad, player); break;
                case "picktype": Cmd_PickType(arena, ad, player, args); break;
                case "nextpick": Cmd_NextPick(arena, ad, player); break;
                case "freezeteams": Cmd_Freeze(arena, ad, player, freeze: true); break;
                case "unfreezeteams": Cmd_Freeze(arena, ad, player, freeze: false); break;
                case "setpickingstage": Cmd_SetPickingStage(arena, ad, player, args); break;
                case "sameteams": Cmd_SameTeams(arena, ad, player); break;

                case "pick": Cmd_AddOrPick(arena, ad, player, args, target, draftCommand: true); break;
                case "add" or "forceadd": Cmd_AddOrPick(arena, ad, player, args, target, draftCommand: false); break;
                case "remove" or "forceremove": Cmd_Remove(arena, ad, player, args, target); break;
                case "ready" or "forceready": Cmd_Ready(arena, ad, player, args); break;
                case "sub" or "forcesub": Cmd_Sub(arena, ad, player, args, target); break;
                case "borrow": Cmd_Borrow(arena, ad, player, args); break;
                case "unborrow": Cmd_UnBorrow(arena, ad, player, args); break;
                case "approve": Cmd_Approve(arena, ad, player, args); break;
                case "addborrow": Cmd_AddBorrow(arena, ad, player, args, target); break;
                case "lagout" or "forcelagout": Cmd_LagOut(arena, ad, player, args, target); break;
                case "teamfreq" or "forceteamfreq": Cmd_TeamFreq(arena, ad, player, args, target); break;

                case "savedteams" or "listsavedteams": Cmd_SavedTeams(player); break;
                case "listsavedteam": Cmd_ListSavedTeam(player, args); break;
                case "delsavedteam": Cmd_DelSavedTeam(player, args); break;
                case "loadteam": Cmd_LoadTeam(arena, ad, player, args); break;
            }
        }

        #endregion

        #region Info commands

        private void Cmd_Teams(Arena arena, ArenaData ad, Player player)
        {
            if (ad.Teams.Count == 0)
            {
                _chat.SendMessage(player, "No teams setup.");
                return;
            }

            foreach (Team team in ad.Teams)
            {
                _chat.SendMessage(player, $"Team {team.TeamName} [{team.Frequency}] - Captain: {team.Captain ?? "(none)"}");
                foreach (TeamPlayer teamPlayer in team.Players)
                {
                    string lag = teamPlayer.LaggedOut && teamPlayer.Ship != SpecShip ? " (Lagged Out)" : "";
                    _chat.SendMessage(player, $"+ {teamPlayer.Name,-24} [{ShipName(teamPlayer.Ship)}]{lag}");
                }
            }
        }

        private void Cmd_Captains(Arena arena, ArenaData ad, Player player)
        {
            if (ad.Teams.Count == 0)
            {
                _chat.SendMessage(player, "No teams setup.");
                return;
            }

            foreach (Team team in ad.Teams)
                _chat.SendMessage(player, $"Team {team.TeamName} [{team.Frequency}] - Captain: {team.Captain ?? "(none)"}");
        }

        private void Cmd_CurrentPick(Arena arena, ArenaData ad, Player player)
        {
            if (ad.PickingStage != PickingStage.Picking)
            {
                _chat.SendMessage(player, "Not currently picking.");
                return;
            }

            if (ad.PickingType == PickingType.Free)
            {
                _chat.SendMessage(player, "Picking is unmanaged. Captains may pick freely.");
                return;
            }

            if (ad.CurrentPickFreq == -1)
            {
                _chat.SendMessage(player, "No team currently has the pick.");
                return;
            }

            Team? team = FindTeamFreq(ad, ad.CurrentPickFreq);
            if (team is null)
            {
                _chat.SendMessage(player, $"No team found with the current pick for frequency {ad.CurrentPickFreq}");
                return;
            }

            if (team.Captain is not null)
                _chat.SendMessage(player, $"{team.Captain} has the current pick for {team.TeamName}.");
            else
                _chat.SendMessage(player, $"{team.TeamName} has the current pick (No Captain Assigned).");
        }

        private void Cmd_ListBorrows(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            // Optional team/freq filter; with no argument, list borrows for every team.
            Team? filter = null;
            if (!args.IsEmpty)
            {
                filter = FindTeam(ad, args);
                if (filter is null)
                {
                    _chat.SendMessage(player, "Team not found.");
                    return;
                }
            }

            bool any = false;
            foreach (Team team in ad.Teams)
            {
                if (filter is not null && team != filter)
                    continue;

                foreach (BorrowedPlayer borrow in team.BorrowList)
                {
                    any = true;
                    if (borrow.Approved)
                        _chat.SendMessage(player, $"[{team.TeamName}] {borrow.Name} [APPROVED by: {borrow.ApprovedBy}]");
                    else
                        _chat.SendMessage(player, $"[{team.TeamName}] {borrow.Name} [PENDING]");
                }
            }

            if (!any)
                _chat.SendMessage(player, "There are no borrows currently.");
        }

        private void DisplayPickStatus(Arena arena, ArenaData ad, Player player)
        {
            _chat.SendMessage(player, $"Stage: {ad.PickingStage}  Draft: {ad.IsDraft}  OfflineDraft: {ad.OfflineDrafting}");
            _chat.SendMessage(player, $"Active event: {ad.ActiveEvent ?? "(none)"}  Repopulate: {ad.RepopulateSignups}");
            _chat.SendMessage(player, $"TeamMax: {MaxToString(ad.TeamMax)}  InGameMax: {MaxToString(ad.TeamInGameMax)}  Teams: {ad.NumberOfTeams}");
            _chat.SendMessage(player, $"Round: {ad.PickingRound}  CurrentPickFreq: {ad.CurrentPickFreq}  PickType: {ad.PickingType}");
            foreach (Team team in ad.Teams)
                _chat.SendMessage(player, $"  [{team.Frequency}] {team.TeamName}  ship:{team.FreqShip}  picked:{team.PickedCount}  cap:{team.Captain ?? "-"}  {(team.Ready ? "READY" : "")} {(team.WasLoaded ? "(LOADED)" : "")}");
        }

        #endregion

        #region Setup commands

        private void Cmd_NewTeams(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            if (ad.PickingStage == PickingStage.GameStart)
            {
                _chat.SendMessage(player, "Game has started. Cannot create new teams until game is ended.");
                return;
            }

            ResetTeams(arena, ad);
            _balls.TrySetBallCount(arena, 0);
            ResetPowerBallStats(arena);

            if (args.StartsWith("-s"))
            {
                if (_db.IsAvailable)
                {
                    ad.SaveTeams = true;
                    _chat.SendMessage(player, "Saving teams to database.");
                }
                else
                {
                    _chat.SendMessage(player, "No DB connection available. Cannot Save teams.");
                }
            }

            _chat.SendArenaMessage(arena, $"Picking of New Teams initiated by {player.Name}");
        }

        private void Cmd_AddTeam(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            if (ad.PickingStage != PickingStage.Setup)
            {
                _chat.SendMessage(player, "Cannot add a team after having started picking.");
                return;
            }

            if (args.IsEmpty)
            {
                _chat.SendMessage(player, "Please specify a frequency. e.g. ?addteam 5:Team Name");
                return;
            }

            SplitColon(args, out ReadOnlySpan<char> freqSpan, out ReadOnlySpan<char> nameSpan);
            ReadOnlySpan<char> freqTrimmed = freqSpan.Trim();
            if (!int.TryParse(freqTrimmed, out int freq) || freq < 0 || (freq == 0 && freqTrimmed[0] != '0'))
            {
                _chat.SendMessage(player, "No frequency specified.");
                return;
            }

            string name = nameSpan.IsEmpty ? $"Team {freq}" : nameSpan.Trim().ToString();
            AddTeam(arena, ad, player, freq, name, isLoaded: false);
        }

        private Team? AddTeam(Arena arena, ArenaData ad, Player? actor, int freq, string name, bool isLoaded)
        {
            if (FindTeamFreq(ad, freq) is not null)
            {
                if (actor is not null)
                    _chat.SendMessage(actor, $"A team already exists on frequency {freq}.");
                return null;
            }

            foreach (Team existing in ad.Teams)
            {
                if (string.Equals(existing.TeamName, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (actor is not null)
                        _chat.SendMessage(actor, $"A team named {name} already exists.");
                    return null;
                }
            }

            // Two teams cannot share a soccer goal (non-draft, Soccer:Mode set).
            int mode = _configManager.GetInt(arena.Cfg!, "Soccer", "Mode", 0);
            int teamsPerGoal = mode < 3 ? 2 : 4;
            if (!ad.IsDraft && mode != 0)
            {
                foreach (Team existing in ad.Teams)
                {
                    if (existing.Frequency % teamsPerGoal == freq % teamsPerGoal)
                    {
                        if (actor is not null)
                            _chat.SendMessage(actor, $"Same goal in use by team on frequency {existing.Frequency}");
                        return null;
                    }
                }
            }

            Team team = new() { Frequency = freq, TeamName = name, WasLoaded = isLoaded };
            ad.Teams.Add(team);
            ad.NumberOfTeams++;

            if (actor is not null)
                _chat.SendArenaMessage(arena, ChatSound.Beep1, $"New team {name} added using frequency {freq}");

            if (ad.SaveTeams && !isLoaded)
                RunDb(() => _db.AddTeamAsync(name, ""));

            return team;
        }

        private void Cmd_RemoveTeam(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            if (ad.PickingStage != PickingStage.Setup)
            {
                _chat.SendMessage(player, "Cannot remove a team after having started picking.");
                return;
            }

            Team? team = FindTeam(ad, args);
            if (team is null)
            {
                _chat.SendMessage(player, "Team not found.");
                return;
            }

            ReleaseTeam(arena, ad, team);
            ad.Teams.Remove(team);
            ad.NumberOfTeams--;

            if (ad.SaveTeams && !team.WasLoaded)
                RunDb(() => _db.DeleteTeamAsync(team.TeamName));

            _chat.SendArenaMessage(arena, ChatSound.Beep1, $"Team {team.TeamName} removed");
        }

        private void Cmd_AddCaptain(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args, ITarget target)
        {
            Team? team;
            string captainName;

            if (target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                team = FindTeam(ad, args);
                captainName = targetPlayer.Name ?? "";
            }
            else
            {
                SplitColon(args, out ReadOnlySpan<char> teamSpan, out ReadOnlySpan<char> nameSpan);
                team = FindTeam(ad, teamSpan.Trim());
                Player? resolved = FindPlayerInArenaFuzzy(arena, nameSpan.Trim());
                captainName = resolved?.Name ?? nameSpan.Trim().ToString();
            }

            if (team is null)
            {
                _chat.SendMessage(player, "Team not found.");
                return;
            }
            if (string.IsNullOrEmpty(captainName))
            {
                _chat.SendMessage(player, "Please specify a player to set as captain.");
                return;
            }

            SetCaptain(arena, ad, team, captainName);
        }

        private void SetCaptain(Arena arena, ArenaData ad, Team team, string captainName)
        {
            if (team.Captain is null)
                _chat.SendArenaMessage(arena, ChatSound.Beep1, $"{captainName} set as captain of team {team.Frequency}");
            else
                _chat.SendArenaMessage(arena, ChatSound.Beep1, $"{team.Captain} replaced with {captainName} as captain of team {team.Frequency}");

            team.Captain = captainName;

            if (ad.SaveTeams)
                RunDb(() => _db.ChangeTeamCaptainAsync(team.TeamName, captainName));
        }

        private void Cmd_RemoveCaptain(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args, ITarget target)
        {
            Team? team;
            string captainQuery;

            if (target.TryGetPlayerTarget(out Player? targeted))
            {
                if (args.IsEmpty)
                {
                    _chat.SendMessage(player, "No frequency or team name specified");
                    return;
                }
                team = FindTeam(ad, args);
                captainQuery = targeted.Name ?? "";
            }
            else
            {
                if (!args.Contains(':'))
                {
                    _chat.SendMessage(player, "You must specify the team name or frequency and the captain. Ex: ?removecap 0:John.");
                    return;
                }
                SplitColon(args, out ReadOnlySpan<char> teamSpan, out ReadOnlySpan<char> nameSpan);
                team = FindTeam(ad, teamSpan.Trim());
                captainQuery = nameSpan.Trim().ToString();
            }

            if (team is null)
            {
                _chat.SendMessage(player, "Team not found.");
                return;
            }

            RemoveCaptain(arena, ad, player, team, captainQuery);
        }

        private void RemoveCaptain(Arena arena, ArenaData ad, Player actor, Team team, string captainQuery)
        {
            if (team.Captain is null)
            {
                _chat.SendMessage(actor, $"There is no captain set for team {team.TeamName}");
                return;
            }

            // The provided string must be a case-insensitive prefix of the current captain's name.
            if (!team.Captain.AsSpan().StartsWith(captainQuery, StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(actor, $"{team.Captain} is the captain of team {team.TeamName}, not {captainQuery}.");
                return;
            }

            _chat.SendArenaMessage(arena, ChatSound.Beep1, $"{team.Captain} removed from captain of team {team.TeamName} by {actor.Name}");
            team.Captain = null;

            if (ad.SaveTeams)
                RunDb(() => _db.ChangeTeamCaptainAsync(team.TeamName, ""));
        }

        private void Cmd_SetTeamShip(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            SplitColon(args, out ReadOnlySpan<char> teamSpan, out ReadOnlySpan<char> shipSpan);
            Team? team = FindTeam(ad, teamSpan.Trim());
            if (team is null || !int.TryParse(shipSpan.Trim(), out int ship) || ship < 1 || ship > 8)
            {
                _chat.SendMessage(player, "Usage: ?setteamship <team>:<1-8>");
                return;
            }

            team.FreqShip = ship - 1;
            _chat.SendMessage(player, $"Team {team.TeamName} ship set to {ship}.");

            _playerData.Lock();
            try
            {
                foreach (TeamPlayer teamPlayer in team.Players)
                {
                    if (teamPlayer.Ship == SpecShip)
                        continue;

                    // Update the stored ship for every rostered in-game player, even if they're momentarily
                    // out of the arena; only the live re-placement is gated on being present.
                    teamPlayer.Ship = team.FreqShip;

                    Player? online = _playerData.FindPlayer(teamPlayer.Name);
                    if (online is not null && online.Arena == arena)
                        _game.SetShipAndFreq(online, (ShipType)team.FreqShip, (short)team.Frequency);
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private void Cmd_TeamMax(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args, bool inGame)
        {
            string label = inGame ? "In-game team max" : "Team max";
            if (args.IsEmpty)
            {
                int cur = inGame ? ad.TeamInGameMax : ad.TeamMax;
                _chat.SendMessage(player, $"{label} currently {MaxToString(cur)}");
                return;
            }

            if (!int.TryParse(args, out int value) || value == 0)
            {
                _chat.SendMessage(player, "Invalid number of players entered");
                return;
            }

            int max = value < 0 ? int.MaxValue : value;
            if (inGame)
                ad.TeamInGameMax = max;
            else
                ad.TeamMax = max;

            _chat.SendMessage(player, $"{label} set to {MaxToString(max)}");
        }

        private void Cmd_Toggle(Arena arena, Player player, ReadOnlySpan<char> args, Func<bool> get, Action<bool> set, string label)
        {
            if (args.IsEmpty)
            {
                _chat.SendMessage(player, $"{label} currently set to {(get() ? "ON" : "OFF")}");
            }
            else if (args.StartsWith("ON", StringComparison.OrdinalIgnoreCase))
            {
                set(true);
                _chat.SendMessage(player, $"{label} set to ON");
            }
            else if (args.StartsWith("OFF", StringComparison.OrdinalIgnoreCase))
            {
                set(false);
                _chat.SendMessage(player, $"{label} set to OFF");
            }
            else
            {
                _chat.SendMessage(player, "Please specify ON or OFF.");
            }
        }

        #endregion

        #region Picking commands

        private void Cmd_StartPicking(Arena arena, ArenaData ad, Player player)
        {
            if (ad.NumberOfTeams < 2)
            {
                _chat.SendMessage(player, "Less than 2 teams setup. Please set teams first.");
                return;
            }

            foreach (Team team in ad.Teams)
            {
                if (team.Captain is null)
                {
                    _chat.SendMessage(player, $"No captain set for Team {team.Frequency}");
                    return;
                }
            }

            StartPicking(arena, ad);
        }

        private void Cmd_PickType(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            if (args.IsEmpty)
            {
                _chat.SendMessage(player, $"Picking Type currently set to: {ad.PickingType}");
                return;
            }

            ad.PickingType = args[0] switch
            {
                '1' => PickingType.Free,
                '2' => PickingType.Normal,
                '3' => PickingType.Snake,
                '4' => PickingType.Random,
                _ => ad.PickingType,
            };

            _chat.SendMessage(player, args[0] is '1' or '2' or '3' or '4'
                ? $"Picking Type set to: {ad.PickingType}"
                : "Invalid option specified.");
        }

        private void Cmd_NextPick(Arena arena, ArenaData ad, Player player)
        {
            NextPick(arena, ad, notify: true, skip: true);
        }

        private void Cmd_Freeze(Arena arena, ArenaData ad, Player player, bool freeze)
        {
            if (freeze && ad.PickingStage == PickingStage.Picking)
            {
                ad.PickingStage = PickingStage.Paused;
                _chat.SendArenaMessage(arena, "Team picking paused.");
            }
            else if (!freeze && ad.PickingStage == PickingStage.Paused)
            {
                ad.PickingStage = PickingStage.Picking;
                _chat.SendArenaMessage(arena, "Team picking resumed.");
            }
            else
            {
                _chat.SendMessage(player, freeze ? "Can only freeze while picking." : "Teams are not paused.");
            }
        }

        private void Cmd_SetPickingStage(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            if (args.IsEmpty)
            {
                DisplayPickStatus(arena, ad, player);
                return;
            }

            // ASSS maps 1->Setup, 2->Picking, 3->Completed, 4->GameStart, 5->GameOver (Paused is not settable here).
            switch (args[0])
            {
                case '1':
                    ad.PickingStage = PickingStage.Setup;
                    _chat.SendMessage(player, "Picking Stage reset to Setup");
                    break;
                case '2':
                    ad.PickingStage = PickingStage.Picking;
                    _chat.SendMessage(player, "Picking Stage reset to Picking");
                    break;
                case '3':
                    ad.PickingStage = PickingStage.Completed;
                    _chat.SendMessage(player, "Picking Stage reset to Completed");
                    break;
                case '4':
                    ad.PickingStage = PickingStage.GameStart;
                    _chat.SendMessage(player, "Picking Stage reset to Game Start");
                    break;
                case '5':
                    ad.PickingStage = PickingStage.GameOver;
                    _chat.SendMessage(player, "Picking Stage reset to Game Over");
                    break;
                default:
                    _chat.SendMessage(player, $"Unrecognized stage {args[0]}, please specify 1-5 as the option");
                    break;
            }
        }

        private void Cmd_SameTeams(Arena arena, ArenaData ad, Player player)
        {
            if (ad.PickingStage != PickingStage.GameOver)
            {
                _chat.SendMessage(player, "Can only reuse the same teams after a game is over.");
                return;
            }

            foreach (Team team in ad.Teams)
                team.Ready = false;

            ad.PickingStage = PickingStage.Picking;
            _chat.SendArenaMessage(arena, ChatSound.Beep1, "A new game using the same teams. Captains to ?ready when ready.");
        }

        #endregion

        #region Placement helpers

        private TeamPlayer AddPlayerToList(Arena arena, ArenaData ad, Team team, string name, int ship, bool wasLoaded, bool wasBorrowed)
        {
            TeamPlayer teamPlayer = new() { Name = name, Ship = ship, WasLoaded = wasLoaded, WasBorrowed = wasBorrowed, LaggedOut = true };
            team.PickedCount++;
            if (ship != SpecShip)
            {
                teamPlayer.LaggedOut = false;
                team.PlayersInGame++;
            }

            team.Players.Add(teamPlayer);

            // Anyone placed on a team is taken off the sign-up list (including loaded/borrowed players), so the
            // list always reflects who is NOT on a team. A no-op if they weren't signed up.
            if (ad.ActiveEvent is not null)
                RemoveFromSignup(ad.ActiveEvent, name);

            return teamPlayer;
        }

        private void PlacePlayer(Arena arena, Player targetPlayer, Team team, int ship)
        {
            _game.SetShipAndFreq(targetPlayer, (ShipType)ship, (short)team.Frequency);
            _scoreStats.ScoreReset(targetPlayer, PersistInterval.Reset);
            _scoreStats.SendUpdates(arena, null);
        }

        private void AddPlayer(Arena arena, ArenaData ad, Team team, Player? targetPlayer, string targetName, Player picker)
        {
            if (team.PickedCount >= ad.TeamMax)
            {
                _chat.SendMessage(picker, "Team maximum has been reached. No more players can be selected.");
                return;
            }

            _chat.SendArenaMessage(arena, ChatSound.Beep1, $"{targetName} picked for team {team.TeamName}");

            int ship = DetermineShip(ad, team);
            AddPlayerToList(arena, ad, team, targetName, ship, wasLoaded: false, wasBorrowed: false);

            if (targetPlayer is not null && targetPlayer.Arena == arena)
                PlacePlayer(arena, targetPlayer, team, ship);

            if (ad.SaveTeams)
                RunDb(() => _db.AddTeamPlayerAsync(team.TeamName, targetName));

            NextPick(arena, ad, notify: true, skip: false);
        }

        private void RemovePlayer(Arena arena, ArenaData ad, Team team, TeamPlayer teamPlayer, Player remover)
        {
            _chat.SendArenaMessage(arena, $"Player {teamPlayer.Name} removed from team {team.TeamName} by {remover.Name}");

            if (teamPlayer.Ship != SpecShip)
                team.PlayersInGame--;

            Player? online = _playerData.FindPlayer(teamPlayer.Name);
            if (online is not null && online.Arena == arena)
            {
                if (team.WasLoaded)
                    _game.SetShipAndFreq(online, ShipType.Spec, (short)team.Frequency);
                else
                    _game.SetShipAndFreq(online, ShipType.Spec, arena.SpecFreq);
            }

            if (team.WasLoaded)
            {
                teamPlayer.Ship = SpecShip;
            }
            else
            {
                team.Players.Remove(teamPlayer);
                team.PickedCount--;

                if (ad.RepopulateSignups && ad.ActiveEvent is not null)
                    RepopulateSignup(ad.ActiveEvent, teamPlayer.Name);

                if (ad.SaveTeams)
                    RunDb(() => _db.RemoveTeamPlayerAsync(team.TeamName, teamPlayer.Name));
            }

            if (ad.PickingStage == PickingStage.Completed)
                NextPick(arena, ad, notify: true, skip: false);
        }

        #endregion

        #region Add / remove / ready / sub

        private void Cmd_AddOrPick(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args, ITarget target, bool draftCommand)
        {
            if (draftCommand && !ad.IsDraft)
            {
                _chat.SendMessage(player, "Currently in team adding mode. Pick is only used for drafting.");
                return;
            }
            if (!draftCommand && ad.IsDraft)
            {
                _chat.SendMessage(player, "Currently in team drafting mode. Add is only used for team picking.");
                return;
            }

            bool isStaff = _capabilityManager.HasCapability(player, "cmd_forceadd");
            bool isCaptain = IsCaptain(ad, player);
            if (!isStaff && !isCaptain)
            {
                _chat.SendMessage(player, "Only captains or Moderators can add players.");
                return;
            }

            if (!isStaff && ad.PickingStage is PickingStage.Setup or PickingStage.Paused or PickingStage.GameOver)
            {
                _chat.SendMessage(player, "Picking currently not available.");
                return;
            }

            // Resolve the target team, the player name, and (when a live player was targeted) the Player object.
            // Mirrors the ASSS CAddHandleInput resolution.
            Team? team = null;
            string targetName;
            Player? targetPlayer = null;
            bool captainPath = false;

            if (target.TryGetPlayerTarget(out Player? targeted))
            {
                if (args.IsEmpty)
                {
                    if (isCaptain)
                    {
                        team = FindCaptainTeam(ad, player);
                        if (team is null)
                        {
                            _chat.SendMessage(player, "Unable to determine your team");
                            return;
                        }
                        captainPath = true;
                    }
                    else
                    {
                        _chat.SendMessage(player, "No frequency or team Name specified.");
                        return;
                    }
                }
                else if (isStaff)
                {
                    // Staff targeting a player while also naming a team/freq: resolve that team.
                    team = FindTeam(ad, args);
                }
                else
                {
                    _chat.SendMessage(player, "No parameters needed if selecting a player as captain.");
                    return;
                }

                targetName = targeted.Name ?? "";
                targetPlayer = targeted;
            }
            else if (args.IsEmpty)
            {
                _chat.SendMessage(player, isCaptain ? "No player name specified." : "No player and no freq nor team name specified");
                return;
            }
            else if (isStaff && args.Contains(':'))
            {
                SplitColon(args, out ReadOnlySpan<char> teamSpan, out ReadOnlySpan<char> nameSpan);
                team = FindTeam(ad, teamSpan.Trim());
                targetName = nameSpan.Trim().ToString();
            }
            else if (isCaptain)
            {
                team = FindCaptainTeam(ad, player);
                if (team is null)
                {
                    _chat.SendMessage(player, "Unable to determine your team");
                    return;
                }
                captainPath = true;
                targetName = args.ToString();
            }
            else
            {
                _chat.SendMessage(player, "No player and freq specified");
                return;
            }

            if (team is null)
            {
                _chat.SendMessage(player, "Team not found.");
                return;
            }
            if (string.IsNullOrEmpty(targetName))
            {
                _chat.SendMessage(player, "Please specify a player.");
                return;
            }

            // Captain turn/stage enforcement (mirrors CheckCaptainPick): applied only on the captain resolution
            // paths and only outside the GameStart/Completed stages.
            if (captainPath && ad.PickingStage is not (PickingStage.GameStart or PickingStage.Completed))
            {
                if (!CheckCaptainPick(player, ad, team))
                    return;
            }

            CheckPlayerAvailable(arena, ad, player, team, targetName, targetPlayer);
        }

        private bool CheckCaptainPick(Player player, ArenaData ad, Team team)
        {
            if (ad.PickingStage == PickingStage.Completed)
            {
                if (!IsTeamMissingPicks(ad, team))
                {
                    _chat.SendMessage(player, "Your team is at the team max. No further picking allowed.");
                    return false;
                }
            }
            else if (!IsCurrentPick(ad, team.Frequency))
            {
                _chat.SendMessage(player, "It is not currently your turn to pick. Please wait.");
                return false;
            }

            return true;
        }

        private void CheckPlayerAvailable(Arena arena, ArenaData ad, Player picker, Team team, string targetName, Player? targetPlayer)
        {
            if (!team.WasLoaded && ad.ActiveEvent is not null)
            {
                UsingSignupList(arena, ad, picker, team, targetName);
                return;
            }

            NotUsingSignupList(arena, ad, picker, team, targetName, targetPlayer);
        }

        private void NotUsingSignupList(Arena arena, ArenaData ad, Player picker, Team team, string targetName, Player? targetPlayer)
        {
            // When the name came from arguments (no live target), require a unique resolution: arena first, then zone-wide.
            if (targetPlayer is null)
            {
                targetPlayer = FindPlayerInArenaFuzzy(arena, targetName, out int arenaCount);
                if (arenaCount > 1)
                {
                    _chat.SendMessage(picker, $"Found {arenaCount} players matching to {targetName}");
                    return;
                }
                if (arenaCount < 1)
                {
                    targetPlayer = FindPlayerOnlineFuzzy(targetName, out int onlineCount);
                    if (onlineCount != 1)
                    {
                        _chat.SendMessage(picker, $"No players matching in the arena and found {onlineCount} players matching to {targetName} online.");
                        return;
                    }
                }
            }

            if (targetPlayer is null)
                return;

            string name = targetPlayer.Name ?? targetName;
            int freq = FindTeamExactPlayer(ad, name)?.Frequency ?? -1;

            if (team.WasLoaded)
            {
                if (freq == team.Frequency)
                {
                    HandleFreqAdd(arena, ad, team, targetPlayer, name, picker);
                    return;
                }

                _chat.SendMessage(picker, $"{name} isn't part of team {team.TeamName} so cannot be added.");
                return;
            }

            if (freq != -1)
            {
                _chat.SendMessage(picker, $"{name} is already on a team!");
                return;
            }

            // Adding a player who is the captain of a *different* team is not allowed.
            int captainFreq = FindCaptainTeam(ad, targetPlayer)?.Frequency ?? -1;
            if (captainFreq != -1 && captainFreq != team.Frequency)
            {
                _chat.SendMessage(picker, $"{name} is the other captain!");
                return;
            }

            AddPlayer(arena, ad, team, targetPlayer, name, picker);
        }

        private void HandleFreqAdd(Arena arena, ArenaData ad, Team team, Player? targetPlayer, string name, Player picker)
        {
            TeamPlayer? teamPlayer = FindPlayerInTeamExact(team, name);
            if (teamPlayer is null)
            {
                _chat.SendMessage(picker, "Cannot find player in the team!");
                return;
            }

            if (teamPlayer.Ship != SpecShip)
            {
                _chat.SendMessage(picker, $"Cannot add {name} as they are already not in spec!");
                return;
            }

            int ship = DetermineShip(ad, team);
            teamPlayer.Ship = ship;
            if (ship != SpecShip)
            {
                teamPlayer.LaggedOut = false;
                team.PlayersInGame++;
            }

            if (targetPlayer is not null && targetPlayer.Arena == arena)
                PlacePlayer(arena, targetPlayer, team, ship);

            _chat.SendArenaMessage(arena, ChatSound.Beep1, $"{name} picked for team {team.TeamName}");
            NextPick(arena, ad, notify: true, skip: false);
        }

        private void Cmd_Remove(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args, ITarget target)
        {
            bool isStaff = _capabilityManager.HasCapability(player, "cmd_forceremove");
            bool isCaptain = IsCaptain(ad, player);
            if (!isStaff && !isCaptain)
            {
                _chat.SendMessage(player, "Only captains or Moderators can remove players.");
                return;
            }

            if (!isStaff && ad.PickingStage is PickingStage.Setup or PickingStage.Paused or PickingStage.GameOver)
            {
                _chat.SendMessage(player, "Picking currently not available.");
                return;
            }

            Team? team = null;
            string name;

            if (target.TryGetPlayerTarget(out Player? targeted))
            {
                if (args.IsEmpty)
                {
                    if (isCaptain)
                    {
                        team = FindCaptainTeam(ad, player);
                        if (team is null)
                        {
                            _chat.SendMessage(player, "Unable to determine your team");
                            return;
                        }
                    }
                    else
                    {
                        _chat.SendMessage(player, "No frequency or team name specified.");
                        return;
                    }
                }
                else if (isStaff)
                {
                    team = FindTeam(ad, args);
                }
                else
                {
                    _chat.SendMessage(player, "No parameters needed if selecting a player.");
                    return;
                }

                name = targeted.Name ?? "";
            }
            else if (args.IsEmpty)
            {
                _chat.SendMessage(player, isCaptain ? "No player name specified." : "No player and frequency or team name specified");
                return;
            }
            else if (isStaff && args.Contains(':'))
            {
                SplitColon(args, out ReadOnlySpan<char> teamSpan, out ReadOnlySpan<char> nameSpan);
                team = FindTeam(ad, teamSpan.Trim());
                name = nameSpan.Trim().ToString();
            }
            else if (isCaptain)
            {
                team = FindCaptainTeam(ad, player);
                if (team is null)
                {
                    _chat.SendMessage(player, "Unable to determine your team");
                    return;
                }
                name = args.ToString();
            }
            else
            {
                _chat.SendMessage(player, "No player and frequency or team name specified");
                return;
            }

            if (team is null)
            {
                _chat.SendMessage(player, "Team not found.");
                return;
            }

            (TeamPlayer? teamPlayer, Team? foundTeam, int count) = FindPlayerInTeamFuzzy(ad, team, name);
            if (count != 1 || teamPlayer is null || foundTeam is null)
            {
                _chat.SendMessage(player, isCaptain
                    ? $"Found {count} players matching to {name} on your team"
                    : $"Found {count} players matching to {name}");
                return;
            }

            RemovePlayer(arena, ad, foundTeam, teamPlayer, player);
        }

        private void Cmd_Ready(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            bool isStaff = _capabilityManager.HasCapability(player, "cmd_forceready");

            if (ad.PickingStage == PickingStage.Completed)
            {
                _chat.SendMessage(player, "Both teams have readied, Cannot un-ready.");
                return;
            }
            if (ad.PickingStage != PickingStage.Picking)
            {
                _chat.SendMessage(player, "Not currently in Picking portion of teams. Cannot ready.");
                return;
            }

            Team? team = isStaff && !args.IsEmpty ? FindTeam(ad, args) : FindCaptainTeam(ad, player);
            if (team is null)
            {
                _chat.SendMessage(player, "Team not found.");
                return;
            }

            SetReady(arena, ad, team, player);

            if (ad.PickingStage == PickingStage.Picking && IsCurrentPick(ad, team.Frequency))
                NextPick(arena, ad, notify: true, skip: false);
        }

        private void Cmd_Sub(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args, ITarget target)
        {
            bool isStaff = _capabilityManager.HasCapability(player, "cmd_forcesub");
            if (!isStaff && !IsCaptain(ad, player))
            {
                _chat.SendMessage(player, "Only captains or staff may sub a player.");
                return;
            }
            if (ad.PickingStage is not (PickingStage.Completed or PickingStage.GameStart))
            {
                _chat.SendMessage(player, "Can only substitute players after picking completed or while game is underway.");
                return;
            }
            if (ad.IsDraft)
            {
                _chat.SendMessage(player, "Cannot substitute players during draft mode.");
                return;
            }

            // Captains are restricted to their own team; staff may sub across teams.
            Team? captainsTeam = null;
            if (!isStaff)
            {
                captainsTeam = FindCaptainTeam(ad, player);
                if (captainsTeam is null)
                {
                    _chat.SendMessage(player, "Cannot determine your team frequency.");
                    return;
                }
            }

            string name1, name2;
            TeamPlayer? tp1, tp2;
            Team? team1, team2;
            Player? sub1, sub2;

            if (target.TryGetPlayerTarget(out Player? targeted))
            {
                if (args.IsEmpty)
                {
                    _chat.SendMessage(player, "Must specify the second player taking part in the substitution.");
                    return;
                }

                name1 = targeted.Name ?? "";
                sub1 = targeted;
                (tp1, team1, _) = FindPlayerInTeamFuzzy(ad, captainsTeam, name1);

                name2 = args.ToString();
                (tp2, team2, int c2) = FindPlayerInTeamFuzzy(ad, captainsTeam, name2);
                if (c2 > 1)
                {
                    _chat.SendMessage(player, $"Found {c2} players matching to {name2}");
                    return;
                }
                sub2 = FindPlayerInArenaFuzzy(arena, name2, out int ac2);
                if (ac2 > 1)
                {
                    _chat.SendMessage(player, $"Found {ac2} players matching to {name2}");
                    return;
                }
            }
            else
            {
                if (args.IsEmpty)
                {
                    _chat.SendMessage(player, "You must specify the 2 players as part of the substitution.");
                    return;
                }
                if (!args.Contains(':'))
                {
                    _chat.SendMessage(player, "Ex: ?sub Paul:John");
                    return;
                }

                SplitColon(args, out ReadOnlySpan<char> s1, out ReadOnlySpan<char> s2);
                name1 = s1.Trim().ToString();
                name2 = s2.Trim().ToString();

                (tp1, team1, int c1) = FindPlayerInTeamFuzzy(ad, captainsTeam, name1);
                if (c1 > 1)
                {
                    _chat.SendMessage(player, $"Found {c1} players matching to {name1}");
                    return;
                }
                (tp2, team2, int c2) = FindPlayerInTeamFuzzy(ad, captainsTeam, name2);
                if (c2 > 1)
                {
                    _chat.SendMessage(player, $"Found {c2} players matching to {name2}");
                    return;
                }
                sub1 = FindPlayerInArenaFuzzy(arena, name1, out int ac1);
                if (ac1 > 1)
                {
                    _chat.SendMessage(player, $"Found {ac1} players matching to {name1}");
                    return;
                }
                sub2 = FindPlayerInArenaFuzzy(arena, name2, out int ac2);
                if (ac2 > 1)
                {
                    _chat.SendMessage(player, $"Found {ac2} players matching to {name2}");
                    return;
                }
            }

            ProcessSubPlayers(arena, ad, player, captainsTeam, tp1, team1, sub1, name1, tp2, team2, sub2, name2);
        }

        // Determines which of the two players is substituted out vs in based on roster membership and in-game
        // status (NOT the colon side): the rostered, in-game player is sent to spec and the other takes their place.
        private void ProcessSubPlayers(Arena arena, ArenaData ad, Player actor, Team? captainsTeam,
            TeamPlayer? tp1, Team? team1, Player? sub1, string name1,
            TeamPlayer? tp2, Team? team2, Player? sub2, string name2)
        {
            bool found1 = tp1 is not null && team1 is not null;
            bool found2 = tp2 is not null && team2 is not null;

            if (!found1 && !found2)
            {
                _chat.SendMessage(actor, "Neither player found in a team");
                return;
            }

            if (found1 && found2)
            {
                if (team1!.Frequency != team2!.Frequency)
                {
                    _chat.SendMessage(actor, $"Players {tp1!.Name} and {tp2!.Name} are not on the same team.");
                    return;
                }

                if (tp1!.Ship == SpecShip)
                {
                    if (tp2!.Ship == SpecShip)
                    {
                        _chat.SendMessage(actor, "Neither player is in game to be subbed.");
                        return;
                    }

                    // player2 is in game (goes out); player1 is in spec (comes in) and must be online.
                    if (sub1 is null)
                    {
                        _chat.SendMessage(actor, $"{tp2.Name} isn't in the arena so cannot be subbed in.");
                        return;
                    }
                    PerformSubstitution(arena, ad, team1, tp2, tp1, sub1, actor);
                }
                else
                {
                    if (tp2!.Ship != SpecShip)
                    {
                        _chat.SendMessage(actor, "Both players are already in game!");
                        return;
                    }
                    if (sub2 is null)
                    {
                        _chat.SendMessage(actor, $"{tp2.Name} isn't in the arena so cannot be subbed in.");
                        return;
                    }
                    PerformSubstitution(arena, ad, team1, tp1, tp2, sub2, actor);
                }

                return;
            }

            if (found1)
            {
                if (captainsTeam is not null && captainsTeam.WasLoaded)
                {
                    _chat.SendMessage(actor, $"Player {tp1!.Name} is not on team {captainsTeam.TeamName}.");
                    return;
                }
                if (tp1!.Ship == SpecShip)
                {
                    _chat.SendMessage(actor, $"{tp1.Name} isn't in game to be substituted!");
                    return;
                }
                if (sub2 is null)
                {
                    _chat.SendMessage(actor, $"{name2} isn't in the arena so cannot be subbed in.");
                    return;
                }

                Team? team = captainsTeam ?? FindTeamExactPlayer(ad, tp1.Name);
                if (team is null)
                {
                    _chat.SendMessage(actor, $"Unable to find team for {tp1.Name}");
                    return;
                }
                TeamPlayer newTp = AddPlayerToList(arena, ad, team, sub2.Name!, SpecShip, wasLoaded: false, wasBorrowed: false);
                PerformSubstitution(arena, ad, team, tp1, newTp, sub2, actor);
                return;
            }

            // Only player2 is on a team.
            if (captainsTeam is not null && captainsTeam.WasLoaded)
            {
                _chat.SendMessage(actor, $"Player {tp2!.Name} is not on team {captainsTeam.TeamName}.");
                return;
            }
            if (tp2!.Ship == SpecShip)
            {
                _chat.SendMessage(actor, $"{tp2.Name} isn't in game to be substituted!");
                return;
            }
            if (sub1 is null)
            {
                _chat.SendMessage(actor, $"{name1} isn't in the arena so cannot be subbed in.");
                return;
            }

            Team? team2Found = captainsTeam ?? FindTeamExactPlayer(ad, tp2.Name);
            if (team2Found is null)
            {
                _chat.SendMessage(actor, $"Unable to find team for {tp2.Name}");
                return;
            }
            TeamPlayer newTp2 = AddPlayerToList(arena, ad, team2Found, sub1.Name!, SpecShip, wasLoaded: false, wasBorrowed: false);
            PerformSubstitution(arena, ad, team2Found, tp2, newTp2, sub1, actor);
        }

        private void PerformSubstitution(Arena arena, ArenaData ad, Team team, TeamPlayer outPlayer, TeamPlayer inPlayer, Player inArena, Player actor)
        {
            int ship = outPlayer.Ship;
            outPlayer.Ship = SpecShip;
            outPlayer.LaggedOut = true;
            inPlayer.Ship = ship;
            inPlayer.LaggedOut = false;

            Player? outArena = _playerData.FindPlayer(outPlayer.Name);
            if (outArena is not null && outArena.Arena == arena)
                _game.SetShipAndFreq(outArena, ShipType.Spec, (short)team.Frequency);

            _game.SetShipAndFreq(inArena, (ShipType)ship, (short)team.Frequency);
            _scoreStats.ScoreReset(inArena, PersistInterval.Reset);
            _scoreStats.SendUpdates(arena, null);

            _chat.SendArenaMessage(arena, $"{outPlayer.Name} substituted with {inPlayer.Name} by {actor.Name}");
        }

        #endregion

        #region Borrow / lagout / teamfreq

        private void Cmd_Borrow(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            Team? team = FindCaptainTeam(ad, player);
            if (team is null)
            {
                _chat.SendMessage(player, "Only captains may request to borrow players.");
                return;
            }
            if (!team.WasLoaded)
            {
                _chat.SendMessage(player, "Borrowing only applies to pre-defined teams.");
                return;
            }
            if (args.IsEmpty)
            {
                _chat.SendMessage(player, "In order to request a borrow, you must provide the player name.");
                return;
            }

            Player? targetPlayer = FindPlayerInArenaFuzzy(arena, args, out int count);
            if (count != 1 || targetPlayer is null)
            {
                _chat.SendMessage(player, $"Found {count} players matching to {args.ToString()}");
                return;
            }

            // Rejected if the target is already a rostered player OR a captain of any team.
            if (FindTeamExactPlayer(ad, targetPlayer.Name!) is not null || FindCaptainTeam(ad, targetPlayer) is not null)
            {
                _chat.SendMessage(player, "Player is already on a team.");
                return;
            }

            (BorrowedPlayer? existing, Team? existingTeam, _) = FindBorrowedPlayerInTeams(ad, targetPlayer.Name!);
            if (existing is not null && existingTeam is not null)
            {
                _chat.SendMessage(player, $"{targetPlayer.Name} is already on the borrow list for {existingTeam.TeamName}");
                return;
            }

            team.BorrowList.Add(new BorrowedPlayer { Name = targetPlayer.Name! });
            _chat.SendArenaMessage(arena, ChatSound.Beep2, $"{team.TeamName} is requesting to borrow {targetPlayer.Name} for this game.");
        }

        private void Cmd_UnBorrow(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            Team? team = FindCaptainTeam(ad, player);
            if (team is null)
            {
                _chat.SendMessage(player, "Only captains may unborrow players.");
                return;
            }
            if (!team.WasLoaded)
            {
                _chat.SendMessage(player, "Borrowing only applies to pre-defined teams.");
                return;
            }
            if (args.IsEmpty)
            {
                _chat.SendMessage(player, "In order to unborrow someone, you must provide the player name.");
                return;
            }

            (BorrowedPlayer? borrow, int count) = FindBorrowedPlayerInTeam(team, args);
            if (count != 1 || borrow is null)
            {
                _chat.SendMessage(player, $"Found {count} players matching to {args.ToString()}");
                return;
            }

            // Removes the borrow request whether or not it had already been approved.
            _chat.SendArenaMessage(arena, ChatSound.Beep2, $"{team.TeamName} removed request to borrow {borrow.Name}");
            team.BorrowList.Remove(borrow);
        }

        private void Cmd_Approve(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            Team? myTeam = FindCaptainTeam(ad, player);
            if (myTeam is null)
            {
                _chat.SendMessage(player, "Only captains may approve borrows.");
                return;
            }
            if (args.IsEmpty)
            {
                _chat.SendMessage(player, "In order to approve someone, you must provide the player name.");
                return;
            }

            (BorrowedPlayer? borrow, Team? team, int count) = FindBorrowedPlayerInTeams(ad, args);
            if (count != 1 || borrow is null || team is null)
            {
                _chat.SendMessage(player, $"Found {count} players matching to {args.ToString()}");
                return;
            }

            if (myTeam == team)
            {
                _chat.SendMessage(player, "You cannot approve your own borrow requests");
                return;
            }

            ApproveBorrowedPlayer(arena, ad, team, borrow, player.Name!);
        }

        private void ApproveBorrowedPlayer(Arena arena, ArenaData ad, Team team, BorrowedPlayer borrow, string approvedBy)
        {
            borrow.Approved = true;
            borrow.ApprovedBy = approvedBy;
            _chat.SendArenaMessage(arena, ChatSound.Beep2, $"{borrow.Name} approved for being borrowed by {approvedBy}.");

            Player? online = _playerData.FindPlayer(borrow.Name);
            AddPlayerToList(arena, ad, team, borrow.Name, SpecShip, wasLoaded: false, wasBorrowed: true);
            if (online is not null && online.Arena == arena)
                _game.SetShipAndFreq(online, ShipType.Spec, (short)team.Frequency);
        }

        private void Cmd_AddBorrow(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args, ITarget target)
        {
            Team? team;
            Player? targetPlayer;

            if (target.TryGetPlayerTarget(out Player? targeted))
            {
                if (args.IsEmpty)
                {
                    _chat.SendMessage(player, "No frequency or team name specified");
                    return;
                }
                team = FindTeam(ad, args);
                targetPlayer = targeted;
            }
            else
            {
                if (!args.Contains(':'))
                {
                    _chat.SendMessage(player, "You must specify the team name or frequency and the player to borrow");
                    return;
                }
                SplitColon(args, out ReadOnlySpan<char> teamSpan, out ReadOnlySpan<char> nameSpan);
                team = FindTeam(ad, teamSpan.Trim());
                targetPlayer = FindPlayerInArenaFuzzy(arena, nameSpan.Trim(), out int count);
                if (count != 1 || targetPlayer is null)
                {
                    _chat.SendMessage(player, $"Found {count} players matching to {nameSpan.Trim().ToString()}");
                    return;
                }
            }

            if (team is null)
            {
                _chat.SendMessage(player, "Team not found.");
                return;
            }
            if (targetPlayer is null)
                return;

            BorrowedPlayer borrow = new() { Name = targetPlayer.Name! };
            team.BorrowList.Add(borrow);
            ApproveBorrowedPlayer(arena, ad, team, borrow, player.Name!);
        }

        private void Cmd_LagOut(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args, ITarget target)
        {
            bool isForce = _capabilityManager.HasCapability(player, "cmd_forcelagout");
            Player subject = player;

            if (target.TryGetPlayerTarget(out Player? targeted) || !args.IsEmpty)
            {
                if (!isForce)
                {
                    _chat.SendMessage(player, "You cannot lagout a player, only yourself.");
                    return;
                }
                if (targeted is not null)
                {
                    subject = targeted;
                }
                else
                {
                    // A name was given but didn't resolve to a unique in-arena player: report it rather than
                    // silently falling back to acting on the staff member.
                    Player? resolved = FindPlayerInArenaFuzzy(arena, args.Trim());
                    if (resolved is null)
                    {
                        _chat.SendMessage(player, $"No unique player found matching {args.Trim().ToString()}");
                        return;
                    }
                    subject = resolved;
                }
            }

            Team? team = FindTeamExactPlayer(ad, subject.Name!);
            TeamPlayer? teamPlayer = team is null ? null : FindPlayerInTeamExact(team, subject.Name!);
            if (team is null || teamPlayer is null)
            {
                _chat.SendMessage(player, $"{subject.Name} is not in a team.");
                return;
            }

            int ship = ad.IsDraft ? SpecShip : teamPlayer.Ship;
            _game.SetShipAndFreq(subject, (ShipType)ship, (short)team.Frequency);
            teamPlayer.LaggedOut = ship == SpecShip;
        }

        private void Cmd_TeamFreq(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args, ITarget target)
        {
            bool isForce = _capabilityManager.HasCapability(player, "cmd_forceteamfreq");
            Player subject = player;

            if (target.TryGetPlayerTarget(out Player? targeted) || !args.IsEmpty)
            {
                if (!isForce)
                {
                    _chat.SendMessage(player, "You cannot set another player's team freq.");
                    return;
                }
                if (targeted is not null)
                {
                    subject = targeted;
                }
                else
                {
                    // A name was given but didn't resolve to a unique in-arena player: report it rather than
                    // silently falling back to acting on the staff member.
                    Player? resolved = FindPlayerInArenaFuzzy(arena, args.Trim());
                    if (resolved is null)
                    {
                        _chat.SendMessage(player, $"No unique player found matching {args.Trim().ToString()}");
                        return;
                    }
                    subject = resolved;
                }
            }

            Team? team = FindTeamExactPlayer(ad, subject.Name!) ?? FindCaptainTeam(ad, subject);
            if (team is null)
            {
                _chat.SendMessage(player, $"No team found for {subject.Name}");
                return;
            }

            _game.SetShipAndFreq(subject, ShipType.Spec, (short)team.Frequency);
        }

        #endregion

        #region Helpers

        private Player? FindPlayerInArenaFuzzy(Arena arena, ReadOnlySpan<char> name) =>
            FindPlayerInArenaFuzzy(arena, name, out _);

        private Player? FindPlayerInArenaFuzzy(Arena arena, ReadOnlySpan<char> name, out int count)
        {
            count = 0;
            if (name.IsEmpty)
                return null;

            Player? match = null;

            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Arena != arena || p.Status != PlayerState.Playing || p.Name is null)
                        continue;

                    if (p.Name.AsSpan().StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.Name.Length == name.Length)
                        {
                            count = 1;
                            return p; // exact
                        }

                        count++;
                        match ??= p;
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return count > 1 ? null : match;
        }

        private Player? FindPlayerOnlineFuzzy(ReadOnlySpan<char> name, out int count)
        {
            count = 0;
            if (name.IsEmpty)
                return null;

            Player? match = null;

            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Status != PlayerState.Playing || p.Name is null)
                        continue;

                    if (p.Name.AsSpan().StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.Name.Length == name.Length)
                        {
                            count = 1;
                            return p; // exact
                        }

                        count++;
                        match ??= p;
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return count > 1 ? null : match;
        }

        private void ResetPowerBallStats(Arena arena)
        {
            IPowerBallStats? stats = arena.GetInterface<IPowerBallStats>();
            if (stats is null)
                return;

            try { stats.ResetStats(arena); }
            finally { arena.ReleaseInterface(ref stats); }
        }

        private static string MaxToString(int value) => value == int.MaxValue ? "unlimited" : value.ToString();

        private static readonly string[] ShipNames =
        [
            "warbird", "javelin", "spider", "leviathan", "terrier", "weasel", "lancaster", "shark", "spectator",
        ];

        private static string ShipName(int ship) => ship >= 0 && ship < ShipNames.Length ? ShipNames[ship] : ship.ToString();

        private void PrintHelp(Player player)
        {
            _chat.SendMessage(player, "----------------------------------------------------");
            _chat.SendMessage(player, "The following Teams Module commands are available:");
            _chat.SendMessage(player, "----------------------------------------------------");
            DisplayCommand(player, "?teams", "List the teams and their players");
            DisplayCommand(player, "?caps", "List the team captains");
            DisplayCommand(player, "?currentpick", "Show whose pick it currently is");
            DisplayCommand(player, "?lagout", "Re-enter the game on your team after lagging/speccing");
            DisplayCommand(player, "?teamfreq", "Join your team's freq in spectator mode");

            if (IsCaptain(player.Arena is { } arena && arena.TryGetExtraData(_adKey, out ArenaData? ad) ? ad : null!, player))
            {
                _chat.SendMessage(player, "-=-=-= Captain Commands =-=-=-");
                DisplayCommand(player, "?pick <name>", "Draft a player onto your team");
                DisplayCommand(player, "?add <name>", "Add a player to your team");
                DisplayCommand(player, "?remove <name>", "Remove a player from your team");
                DisplayCommand(player, "?ready", "Mark your team ready");
                DisplayCommand(player, "?sub <in>:<out>", "Substitute players");
                DisplayCommand(player, "?borrow <name>", "Request to borrow a player (loaded teams)");
                DisplayCommand(player, "?approve <name>", "Approve another team's borrow request");
            }

            bool displayedMod = false;
            DisplayModCommand(player, ref displayedMod, "newteams", "?newteams [-s]", "Reset and start new teams (-s to save to DB)");
            DisplayModCommand(player, ref displayedMod, "addteam", "?addteam <freq>[:name]", "Add a team");
            DisplayModCommand(player, ref displayedMod, "addcaptain", "?addcap <team>:<name>", "Set a team captain");
            DisplayModCommand(player, ref displayedMod, "startpicking", "?startpicking", "Begin the draft");
            DisplayModCommand(player, ref displayedMod, "picktype", "?picktype [1-4]", "Set the pick order (Free/Normal/Snake/Random)");
            DisplayModCommand(player, ref displayedMod, "nextpick", "?nextpick", "Advance to the next pick");
            DisplayModCommand(player, ref displayedMod, "draftmode", "?draftmode [ON/OFF]", "Toggle draft vs pick mode");
            DisplayModCommand(player, ref displayedMod, "usesignups", "?usesignups [event/OFF]", "Restrict drafting to a sign-up event");
            DisplayModCommand(player, ref displayedMod, "loadteam", "?loadteam <freq>:<name>", "Load a saved team");
            DisplayModCommand(player, ref displayedMod, "savedteams", "?savedteams", "List saved teams");
            DisplayModCommand(player, ref displayedMod, "pickstatus", "?pickstatus", "Show detailed picking status");
        }

        private void DisplayCommand(Player player, string command, string description)
        {
            _chat.SendMessage(player, $"{command,-35} - {description}");
        }

        private void DisplayModCommand(Player player, ref bool displayedMod, string capability, string command, string description)
        {
            if (!_capabilityManager.HasCapability(player, $"cmd_{capability}"))
                return;

            if (!displayedMod)
            {
                displayedMod = true;
                _chat.SendMessage(player, "-=-=-= Moderator Commands =-=-=-");
            }

            _chat.SendMessage(player, $"{command,-35} - {description}");
        }

        #endregion
    }
}
