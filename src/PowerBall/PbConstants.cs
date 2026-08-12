using SS.Core;

namespace SS.PowerBall
{
    /// <summary>
    /// LVZ object IDs used by the PowerBall LVZ file. Ported from <c>pblvzs.h</c> and <c>small4tmpb.h</c>.
    /// </summary>
    internal static class PbLvz
    {
        // Banners / state (timed objects show for a fixed duration then hide themselves).
        public const short GameOver = 50;
        public const short NewGame = 53;

        // "Has the ball" indicators.
        public const short WarbirdBall = 40;
        public const short JavelinBall = 41;

        // "Leading" indicators.
        public const short WarbirdLeading = 44;
        public const short JavelinLeading = 45;

        // "Game point" indicators.
        public const short WarbirdGamePoint = 46;
        public const short JavelinGamePoint = 47;

        // Score sparkle animations (timed).
        public const short WarbirdScoreSparkle = 48;
        public const short JavelinScoreSparkle = 49;

        // Win banners (timed).
        public const short WarbirdWin = 51;
        public const short JavelinWin = 52;

        // Game clock digit bases (each is the "0" digit; add the digit value).
        public const short Timer = 57;
        public const short SecondsZero = 58;
        public const short SecondsCountdown = 59;
        public const short TenSecondsZero = 60;
        public const short MinutesZero = 70;
        public const short TenMinutesZero = 80;

        // Animated score digit object bases (digit object id = base + digit value 0..9).
        public const short WarbirdScoreOnes = 0;   // ids 0..9
        public const short WarbirdScoreTens = 10;  // ids 10..19
        public const short JavelinScoreOnes = 20;  // ids 20..29
        public const short JavelinScoreTens = 30;  // ids 30..39

        // Small-4-team score digit base. Team i, score s => Small4TeamScore0 + s + 5*i.
        public const short Small4TeamScore0 = 2500;

        // League match countdown banners.
        public const short LeagueReady = 91;
        public const short LeagueSet = 92;
        public const short LeagueGo = 93;
    }

    /// <summary>
    /// Zone-specific "bong" sound IDs used by the PowerBall LVZ/sound set. Ported from <c>pblvzs.h</c>.
    /// These are custom sound files in the zone's sound set, so they are cast from their numeric IDs.
    /// </summary>
    internal static class PbSound
    {
        public const ChatSound GameOver = (ChatSound)30;
        public const ChatSound Lockdown = (ChatSound)31;
        public const ChatSound WarbirdScore = (ChatSound)50;
        public const ChatSound WarbirdGamePoint = (ChatSound)51;
        public const ChatSound WarbirdWin = (ChatSound)52;
        public const ChatSound JavelinScore = (ChatSound)70;
        public const ChatSound JavelinGamePoint = (ChatSound)71;
        public const ChatSound JavelinWin = (ChatSound)72;

        // Scramble countdown sounds (raw zone sound IDs).
        public const ChatSound Ready = (ChatSound)91;
        public const ChatSound Go = (ChatSound)93;
    }
}
