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
                case "pickstatus" or "setpickingstage" when args.IsEmpty: DisplayPickStatus(arena, ad, player); break;
                case "listborrows": Cmd_ListBorrows(arena, ad, player, args); break;
                case "teamshelp": PrintHelp(player); break;
                case "teamsversion": _chat.SendMessage(player, "PowerBall Teams module (C# port of ASSS Teams Module v1.1 by POiD)"); break;

                case "newteams": Cmd_NewTeams(arena, ad, player, args); break;
                case "addteam": Cmd_AddTeam(arena, ad, player, args); break;
                case "removeteam": Cmd_RemoveTeam(arena, ad, player, args); break;
                case "addcap" or "addcaptain": Cmd_AddCaptain(arena, ad, player, args, target); break;
                case "removecap" or "removecaptain": Cmd_RemoveCaptain(arena, ad, player, args); break;
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
                case "addborrow": Cmd_AddBorrow(arena, ad, player, args); break;
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
                    _chat.SendMessage(player, $"+ {teamPlayer.Name,-24} [{teamPlayer.Ship}]{lag}");
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

            Team? team = FindTeamFreq(ad, ad.CurrentPickFreq);
            if (team is null)
                return;

            if (team.Captain is not null)
                _chat.SendMessage(player, $"{team.Captain} has the current pick for {team.TeamName}.");
            else
                _chat.SendMessage(player, $"{team.TeamName} has the current pick (No Captain Assigned).");
        }

        private void Cmd_ListBorrows(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            bool any = false;
            foreach (Team team in ad.Teams)
            {
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
            if (!int.TryParse(freqSpan.Trim(), out int freq))
            {
                _chat.SendMessage(player, "Invalid frequency.");
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
                _chat.SendArenaMessage(arena, $"New team {name} added using frequency {freq}");

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

            _chat.SendArenaMessage(arena, $"Team {team.TeamName} removed");
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
                _chat.SendArenaMessage(arena, $"{captainName} set as captain of team {team.Frequency}");
            else
                _chat.SendArenaMessage(arena, $"{team.Captain} replaced with {captainName} as captain of team {team.Frequency}");

            team.Captain = captainName;

            if (ad.SaveTeams)
                RunDb(() => _db.ChangeTeamCaptainAsync(team.TeamName, captainName));
        }

        private void Cmd_RemoveCaptain(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            SplitColon(args, out ReadOnlySpan<char> teamSpan, out ReadOnlySpan<char> nameSpan);
            Team? team = FindTeam(ad, teamSpan.Trim());
            if (team is null || team.Captain is null)
            {
                _chat.SendMessage(player, "Team or captain not found.");
                return;
            }

            if (!nameSpan.IsEmpty && !nameSpan.Trim().Equals(team.Captain, StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(player, $"{nameSpan.Trim().ToString()} is not the captain of {team.TeamName}.");
                return;
            }

            _chat.SendArenaMessage(arena, $"{team.Captain} removed as captain of team {team.Frequency}");
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

                    Player? online = _playerData.FindPlayer(teamPlayer.Name);
                    if (online is not null && online.Arena == arena)
                    {
                        teamPlayer.Ship = team.FreqShip;
                        _game.SetShipAndFreq(online, (ShipType)team.FreqShip, (short)team.Frequency);
                    }
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
            if (!int.TryParse(args, out int stage) || stage < 1 || stage > 5)
            {
                _chat.SendMessage(player, "Usage: ?setpickingstage <1-5>");
                return;
            }

            ad.PickingStage = (PickingStage)stage;
            _chat.SendMessage(player, $"Picking stage set to {ad.PickingStage}.");
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
            _chat.SendArenaMessage(arena, "A new game using the same teams. Captains to ?ready when ready.");
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

            if (ad.ActiveEvent is not null && !(wasLoaded || wasBorrowed))
                RemoveFromSignup(ad.ActiveEvent, name);

            return teamPlayer;
        }

        private void PlacePlayer(Arena arena, Player targetPlayer, Team team, int ship)
        {
            _game.SetShipAndFreq(targetPlayer, (ShipType)ship, (short)team.Frequency);
            _scoreStats.ScoreReset(targetPlayer, PersistInterval.Reset);
            _scoreStats.SendUpdates(arena, null);
        }

        private void AddPlayer(Arena arena, ArenaData ad, Team team, Player? targetPlayer, string targetName)
        {
            if (team.PickedCount >= ad.TeamMax)
            {
                _chat.SendArenaMessage(arena, "Team maximum has been reached. No more players can be selected.");
                return;
            }

            _chat.SendArenaMessage(arena, $"{targetName} picked for team {team.TeamName}");

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
            if (!draftCommand && ad.IsDraft && false)
            {
                // ?add in draft mode is still allowed for staff in ASSS; leave add usable.
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

            // Resolve the target team and player name.
            Team? team;
            string targetName;
            if (target.TryGetPlayerTarget(out Player? targeted))
            {
                team = isStaff ? FindCaptainTeam(ad, player) ?? (ad.Teams.Count > 0 ? ad.Teams[0] : null) : FindCaptainTeam(ad, player);
                targetName = targeted.Name ?? "";
            }
            else if (isStaff && args.Contains(':'))
            {
                SplitColon(args, out ReadOnlySpan<char> teamSpan, out ReadOnlySpan<char> nameSpan);
                team = FindTeam(ad, teamSpan.Trim());
                targetName = nameSpan.Trim().ToString();
            }
            else
            {
                team = FindCaptainTeam(ad, player);
                targetName = args.ToString();
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

            // Captain turn enforcement.
            if (isCaptain && !isStaff && ad.PickingStage is not (PickingStage.GameStart or PickingStage.Completed))
            {
                if (ad.PickingStage == PickingStage.Completed && !IsTeamMissingPicks(ad, team))
                {
                    _chat.SendMessage(player, "Your team is at the team max. No further picking allowed.");
                    return;
                }
                if (!IsCurrentPick(ad, team.Frequency))
                {
                    _chat.SendMessage(player, "It is not currently your turn to pick. Please wait.");
                    return;
                }
            }

            CheckPlayerAvailable(arena, ad, player, team, targetName);
        }

        private void CheckPlayerAvailable(Arena arena, ArenaData ad, Player picker, Team team, string targetName)
        {
            if (!team.WasLoaded && ad.ActiveEvent is not null)
            {
                UsingSignupList(arena, ad, picker, team, targetName);
                return;
            }

            NotUsingSignupList(arena, ad, picker, team, targetName);
        }

        private void NotUsingSignupList(Arena arena, ArenaData ad, Player picker, Team team, string targetName)
        {
            Player? targetPlayer = FindPlayerInArenaFuzzy(arena, targetName) ?? _playerData.FindPlayer(targetName);
            string name = targetPlayer?.Name ?? targetName;

            if (team.WasLoaded)
            {
                // Only allow if the target is part of this loaded team's roster.
                if (FindPlayerInTeamExact(team, name) is null)
                {
                    _chat.SendMessage(picker, $"{name} isn't part of team {team.TeamName} so cannot be added.");
                    return;
                }
                HandleFreqAdd(arena, ad, team, targetPlayer, name);
                return;
            }

            Team? owner = FindTeamExactPlayer(ad, name);
            if (owner is not null)
            {
                _chat.SendMessage(picker, $"{name} is already on a team!");
                return;
            }

            AddPlayer(arena, ad, team, targetPlayer, name);
        }

        private void HandleFreqAdd(Arena arena, ArenaData ad, Team team, Player? targetPlayer, string name)
        {
            TeamPlayer? teamPlayer = FindPlayerInTeamExact(team, name);
            if (teamPlayer is null)
                return;

            if (teamPlayer.Ship != SpecShip)
            {
                _chat.SendArenaMessage(arena, $"Cannot add {name} as they are already not in spec!");
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

            _chat.SendArenaMessage(arena, $"{name} picked for team {team.TeamName}");
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

            Team? scope = isStaff ? null : FindCaptainTeam(ad, player);
            string name = target.TryGetPlayerTarget(out Player? targeted) ? targeted.Name ?? "" : args.ToString();

            (TeamPlayer? teamPlayer, Team? team, _) = FindPlayerInTeamFuzzy(ad, scope, name);
            if (teamPlayer is null || team is null)
            {
                _chat.SendMessage(player, $"Player not found matching {name}");
                return;
            }

            RemovePlayer(arena, ad, team, teamPlayer, player);
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
                _chat.SendMessage(player, "Only captains or Moderators can substitute.");
                return;
            }
            if (ad.PickingStage is not (PickingStage.Completed or PickingStage.GameStart))
            {
                _chat.SendMessage(player, "Substitution is only available after picking.");
                return;
            }
            if (ad.IsDraft)
            {
                _chat.SendMessage(player, "Cannot substitute players during draft mode.");
                return;
            }

            SplitColon(args, out ReadOnlySpan<char> inSpan, out ReadOnlySpan<char> outSpan);
            Team? scope = isStaff ? null : FindCaptainTeam(ad, player);

            (TeamPlayer? outPlayer, Team? outTeam, _) = FindPlayerInTeamFuzzy(ad, scope, outSpan.Trim());
            Player? inArena = FindPlayerInArenaFuzzy(arena, inSpan.Trim());

            if (outPlayer is null || outTeam is null)
            {
                _chat.SendMessage(player, "Player to sub out not found on a team.");
                return;
            }
            if (inArena is null)
            {
                _chat.SendMessage(player, $"{inSpan.Trim().ToString()} isn't in the arena so cannot be subbed in.");
                return;
            }
            if (outPlayer.Ship == SpecShip)
            {
                _chat.SendMessage(player, "The player to sub out is not in game.");
                return;
            }

            // Ensure the sub-in is on the team (append as spec if needed, non-loaded only).
            TeamPlayer? inPlayer = FindPlayerInTeamExact(outTeam, inArena.Name!);
            if (inPlayer is null)
            {
                if (outTeam.WasLoaded)
                {
                    _chat.SendMessage(player, $"Player {inArena.Name} is not on team {outTeam.TeamName}.");
                    return;
                }
                inPlayer = AddPlayerToList(arena, ad, outTeam, inArena.Name!, SpecShip, wasLoaded: false, wasBorrowed: false);
            }

            PerformSubstitution(arena, ad, outTeam, outPlayer, inPlayer, inArena, player);
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
            if (team is null || !team.WasLoaded)
            {
                _chat.SendMessage(player, "Borrowing only applies to pre-defined teams.");
                return;
            }

            string name = args.Trim().ToString();
            if (FindTeamExactPlayer(ad, name) is not null)
            {
                _chat.SendMessage(player, "Player is already on a team.");
                return;
            }

            (BorrowedPlayer? existing, _) = FindBorrowedPlayerInTeams(ad, name);
            if (existing is not null)
            {
                _chat.SendMessage(player, $"{name} is already on the borrow list for {team.TeamName}");
                return;
            }

            team.BorrowList.Add(new BorrowedPlayer { Name = name });
            _chat.SendArenaMessage(arena, ChatSound.Beep2, $"{player.Name} is requesting to borrow {name} for this game.");
        }

        private void Cmd_UnBorrow(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            Team? team = FindCaptainTeam(ad, player);
            if (team is null)
                return;

            string name = args.Trim().ToString();
            foreach (BorrowedPlayer borrow in team.BorrowList)
            {
                if (!borrow.Approved && name.Equals(borrow.Name, StringComparison.OrdinalIgnoreCase))
                {
                    team.BorrowList.Remove(borrow);
                    _chat.SendArenaMessage(arena, $"{player.Name} removed request to borrow {name}");
                    return;
                }
            }

            _chat.SendMessage(player, "No pending borrow found for that player.");
        }

        private void Cmd_Approve(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            if (!IsCaptain(ad, player))
                return;

            string name = args.Trim().ToString();
            (BorrowedPlayer? borrow, Team? team) = FindBorrowedPlayerInTeams(ad, name);
            if (borrow is null || team is null)
            {
                _chat.SendMessage(player, "No pending borrow found for that player.");
                return;
            }

            Team? myTeam = FindCaptainTeam(ad, player);
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
            _chat.SendArenaMessage(arena, $"{borrow.Name} approved for being borrowed by {approvedBy}.");

            Player? online = _playerData.FindPlayer(borrow.Name);
            AddPlayerToList(arena, ad, team, borrow.Name, SpecShip, wasLoaded: false, wasBorrowed: true);
            if (online is not null && online.Arena == arena)
                _game.SetShipAndFreq(online, ShipType.Spec, (short)team.Frequency);
        }

        private void Cmd_AddBorrow(Arena arena, ArenaData ad, Player player, ReadOnlySpan<char> args)
        {
            SplitColon(args, out ReadOnlySpan<char> teamSpan, out ReadOnlySpan<char> nameSpan);
            Team? team = FindTeam(ad, teamSpan.Trim());
            if (team is null)
            {
                _chat.SendMessage(player, "Team not found.");
                return;
            }

            string name = nameSpan.Trim().ToString();
            BorrowedPlayer borrow = new() { Name = name };
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
                subject = targeted ?? FindPlayerInArenaFuzzy(arena, args.Trim()) ?? player;
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
                subject = targeted ?? FindPlayerInArenaFuzzy(arena, args.Trim()) ?? player;
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

        private Player? FindPlayerInArenaFuzzy(Arena arena, ReadOnlySpan<char> name)
        {
            if (name.IsEmpty)
                return null;

            Player? match = null;
            int count = 0;

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
                            return p; // exact

                        count++;
                        match ??= p;
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return count == 1 ? match : (count > 1 ? null : match);
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
