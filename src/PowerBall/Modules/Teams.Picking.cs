using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.PowerBall.ComponentCallbacks;
using System;

namespace SS.PowerBall.Modules
{
    public sealed partial class Teams
    {
        private const int SpecShip = (int)ShipType.Spec; // 8

        #region Start picking / dispatch

        private void StartPicking(Arena arena, ArenaData ad)
        {
            if (ad.PickingType == PickingType.Random)
                SortTeamsRandomly(ad);
            else
                SortTeamsByFreq(ad);

            ad.CurrentPick = 1;
            ad.PickingRound = 1;
            ad.PickDirection = false;
            ad.CurrentPickFreq = -1;
            ad.PickingStage = PickingStage.Picking;

            _game.LockArena(arena, true, false, false, true);

            _chat.SendArenaMessage(arena, ad.PickingType switch
            {
                PickingType.Free => "Picking has started (Free / uncontrolled order).",
                PickingType.Normal => "Picking has started (one after another).",
                PickingType.Snake => "Picking has started (snake order).",
                PickingType.Random => "Picking has started (random order).",
                _ => "Picking has started.",
            });

            DispatchPick(arena, ad, notify: true, skip: false);
        }

        private void DispatchPick(Arena arena, ArenaData ad, bool notify, bool skip)
        {
            switch (ad.PickingType)
            {
                case PickingType.Normal:
                    NormalPick(arena, ad, notify);
                    break;
                case PickingType.Snake:
                    SnakePick(arena, ad, notify, skip);
                    break;
                case PickingType.Random:
                    RandomPick(arena, ad, notify);
                    break;
                case PickingType.Free:
                default:
                    if (skip)
                        _chat.SendArenaMessage(arena, "Picking is currently not controlled. Captains may pick freely.");
                    break;
            }
        }

        /// <summary>Called after each successful pick to advance the draft.</summary>
        private void NextPick(Arena arena, ArenaData ad, bool notify, bool skip)
        {
            if (FindMissingPicks(arena, ad, notify, skip))
                return;

            if (SetPickingRound(arena, ad))
                return;

            DispatchPick(arena, ad, notify, skip);
        }

        #endregion

        #region Pick orders

        private void NormalPick(Arena arena, ArenaData ad, bool notify)
        {
            SortTeamsByFreq(ad);

            if (ad.CurrentPick > ad.NumberOfTeams)
                ad.CurrentPick = 1;

            int passes = 0;
            while (passes < 2)
            {
                int pos = 0;
                foreach (Team team in ad.Teams)
                {
                    pos++;
                    if (pos < ad.CurrentPick)
                        continue;

                    if (team.WasLoaded && team.PlayersInGame >= ad.TeamInGameMax)
                        continue;

                    ad.CurrentPickFreq = team.Frequency;
                    if (notify)
                        AnnouncePick(arena, team);

                    ad.CurrentPick++;
                    if (ad.CurrentPick > ad.NumberOfTeams)
                        ad.CurrentPick = 1;
                    return;
                }

                // Reached the end without a pick; wrap and try once more.
                ad.CurrentPick = 1;
                passes++;
            }

            AnnouncePickingComplete(arena, ad);
        }

        private void SnakePick(Arena arena, ArenaData ad, bool notify, bool skip)
        {
            SortTeamsByFreq(ad);

            int loopCount = 0;
            while (loopCount < 2)
            {
                int pos = 0;
                foreach (Team team in ad.Teams)
                {
                    pos++;
                    if (pos != ad.CurrentPick)
                        continue;

                    bool eligible = !(team.WasLoaded && team.PlayersInGame >= ad.TeamInGameMax)
                        && !(skip && team.Frequency == ad.CurrentPickFreq);

                    if (eligible)
                    {
                        ad.CurrentPickFreq = team.Frequency;
                        if (notify)
                            AnnouncePick(arena, team);
                        AdvanceSnake(ad);
                        return;
                    }

                    break;
                }

                AdvanceSnake(ad);
                loopCount++;
            }

            AnnouncePickingComplete(arena, ad);
        }

        private static void AdvanceSnake(ArenaData ad)
        {
            if (!ad.PickDirection)
            {
                ad.CurrentPick++;
                if (ad.CurrentPick > ad.NumberOfTeams)
                {
                    ad.CurrentPick = ad.NumberOfTeams; // the end team picks twice
                    ad.PickDirection = true;
                }
            }
            else
            {
                ad.CurrentPick--;
                if (ad.CurrentPick < 1)
                {
                    ad.CurrentPick = 1; // the first team picks twice
                    ad.PickDirection = false;
                }
            }
        }

        private void RandomPick(Arena arena, ArenaData ad, bool notify)
        {
            // Count teams eligible to pick this round.
            int eligible = 0;
            foreach (Team team in ad.Teams)
            {
                if (IsEligibleThisRound(ad, team))
                    eligible++;
            }

            int pickCount;
            if (eligible == 0)
            {
                ad.PickingRound++;
                pickCount = ad.NumberOfTeams > 0 ? _prng.Number(0, ad.NumberOfTeams - 1) : 0;
            }
            else
            {
                pickCount = _prng.Number(0, eligible - 1);
            }

            foreach (Team team in ad.Teams)
            {
                if (team.PickedCount >= ad.PickingRound)
                    continue;

                if (pickCount-- > 0)
                    continue;

                ad.CurrentPickFreq = team.Frequency;
                if (notify)
                    AnnouncePick(arena, team);
                return;
            }

            AnnouncePickingComplete(arena, ad);
        }

        private static bool IsEligibleThisRound(ArenaData ad, Team team)
        {
            if (team.WasLoaded)
                return team.PlayersInGame < ad.TeamInGameMax && team.PlayersInGame < ad.PickingRound;

            return team.PickedCount < ad.PickingRound;
        }

        #endregion

        #region Round management

        /// <summary>Finds the team most behind on picks and gives it the pick. Returns true if one was found.</summary>
        private bool FindMissingPicks(Arena arena, ArenaData ad, bool notify, bool skip)
        {
            Team? behind = null;
            foreach (Team team in ad.Teams)
            {
                if (skip && team.Frequency == ad.CurrentPickFreq)
                    continue;

                int count = team.WasLoaded ? team.PlayersInGame : team.PickedCount;
                if (count < ad.PickingRound - 1)
                {
                    behind = team;
                    break;
                }
            }

            if (behind is null)
                return false;

            ad.CurrentPickFreq = behind.Frequency;
            if (notify)
            {
                if (behind.Captain is not null)
                    _chat.SendArenaMessage(arena, $"Your pick {behind.Captain}");
                else
                    _chat.SendArenaMessage(arena, $"{behind.TeamName} team pick, but no captain set!");
            }

            return true;
        }

        /// <summary>Advances the picking round. Returns true when all rounds are complete.</summary>
        private bool SetPickingRound(Arena arena, ArenaData ad)
        {
            foreach (Team team in ad.Teams)
            {
                int count = team.WasLoaded ? team.PlayersInGame : team.PickedCount;
                if (count < ad.PickingRound)
                    return false; // some team still owes a pick this round
            }

            ad.PickingRound++;

            bool anyNonLoaded = false;
            foreach (Team team in ad.Teams)
            {
                if (!team.WasLoaded)
                    anyNonLoaded = true;
            }

            int cap = anyNonLoaded ? ad.TeamMax : ad.TeamInGameMax;
            if (ad.PickingRound > cap)
            {
                if (ad.PickingStage == PickingStage.Picking)
                    _chat.SendArenaMessage(arena, "Teams have picked the maximum number of players. Captains to ?ready when ready.");
                return true;
            }

            return false;
        }

        private bool IsTeamMissingPicks(ArenaData ad, Team team)
        {
            int count = team.WasLoaded ? team.PlayersInGame : team.PickedCount;
            int cap = team.WasLoaded ? ad.TeamInGameMax : (ad.PickingStage == PickingStage.Completed ? ad.TeamMax : ad.PickingRound);
            return count < cap;
        }

        private bool IsCurrentPick(ArenaData ad, int freq)
        {
            return ad.PickingType == PickingType.Free || ad.CurrentPickFreq == freq;
        }

        #endregion

        #region Ready

        private void SetReady(Arena arena, ArenaData ad, Team team, Player? setter)
        {
            team.Ready = !team.Ready;
            _chat.SendArenaMessage(arena, $"Team {team.TeamName} ready status set to {(team.Ready ? "READY" : "NOT READY")} by {setter?.Name ?? "staff"}");

            CheckReady(arena, ad);
        }

        private void CheckReady(Arena arena, ArenaData ad)
        {
            if (ad.NumberOfTeams < 1)
                return;

            foreach (Team team in ad.Teams)
            {
                if (!team.Ready)
                    return; // not all ready
            }

            if (ad.IsDraft)
            {
                ad.PickingStage = PickingStage.Completed;
            }
            else
            {
                TeamsReadyCallback.Fire(arena, arena);
                ad.PickingStage = PickingStage.GameStart;
                // Note: the ball game is started by the PowerBallLeague module in response to TeamsReady.
            }
        }

        #endregion

        #region Ship assignment

        private int DetermineShip(ArenaData ad, Team team)
        {
            if (ad.IsDraft)
                return SpecShip;

            if (team.PlayersInGame >= ad.TeamInGameMax)
                return SpecShip;

            if (team.FreqShip == -1)
                return ad.NumberOfTeams > 0 ? (team.Frequency % ad.NumberOfTeams) % 8 : 0;

            return team.FreqShip;
        }

        #endregion

        #region Sorting / announcements

        private static void SortTeamsByFreq(ArenaData ad)
        {
            ad.Teams.Sort(static (a, b) => a.Frequency.CompareTo(b.Frequency));
        }

        private void SortTeamsRandomly(ArenaData ad)
        {
            // Fisher-Yates using the server PRNG.
            for (int i = ad.Teams.Count - 1; i > 0; i--)
            {
                int j = _prng.Number(0, i);
                (ad.Teams[i], ad.Teams[j]) = (ad.Teams[j], ad.Teams[i]);
            }
        }

        private void AnnouncePick(Arena arena, Team team)
        {
            if (team.Captain is not null)
                _chat.SendArenaMessage(arena, $"Your pick {team.Captain}");
            else
                _chat.SendArenaMessage(arena, $"Your pick {team.TeamName} (No Captain assigned)");
        }

        private void AnnouncePickingComplete(Arena arena, ArenaData ad)
        {
            _chat.SendArenaMessage(arena, "Picking complete. Captains to ?ready when ready.");
        }

        #endregion
    }
}
