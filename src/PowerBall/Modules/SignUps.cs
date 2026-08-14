using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.PowerBall.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// Event sign-up lists — a port of the ASSS <c>signups</c> module. Staff create/activate named events; players
    /// <c>?signup</c>. The Teams module consumes the list to restrict who can be drafted.
    /// </summary>
    /// <remarks>
    /// The ASSS original used a callback-based async DB; this port uses <c>async</c>/<c>await</c> over
    /// <see cref="ILeagueDatabase"/>. Only chat side effects happen after an <c>await</c>, so no mainloop re-marshaling is
    /// needed (chat is thread-safe). Names are matched case-insensitively.
    /// </remarks>
    [ModuleInfo("Event sign-up lists (ASSS signups port). Requires the PowerBallDatabase module.")]
    public sealed class SignUps : IModule, IArenaAttachableModule, ISignUps
    {
        private const string DbUnavailable = "Unfortunately the database is currently not available. Cannot process request.";

        private readonly ILeagueDatabase _db;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly ICapabilityManager _capabilityManager;
        private readonly ILogManager _logManager;

        private InterfaceRegistrationToken<ISignUps>? _iSignUpsToken;

        public SignUps(
            ILeagueDatabase db,
            IChat chat,
            ICommandManager commandManager,
            ICapabilityManager capabilityManager,
            ILogManager logManager)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _iSignUpsToken = broker.RegisterInterface<ISignUps>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iSignUpsToken) != 0)
                return false;

            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _commandManager.AddCommand("signupshelp", Command_signupshelp, arena);
            _commandManager.AddCommand("signupsversion", Command_signupsversion, arena);
            _commandManager.AddCommand("addevent", Command_addevent, arena);
            _commandManager.AddCommand("delevent", Command_delevent, arena);
            _commandManager.AddCommand("chgevent", Command_chgevent, arena);
            _commandManager.AddCommand("listevents", Command_listevents, arena);
            _commandManager.AddCommand("startsignups", Command_startsignups, arena);
            _commandManager.AddCommand("endsignups", Command_endsignups, arena);
            _commandManager.AddCommand("listsignups", Command_listsignups, arena);
            _commandManager.AddCommand("signups", Command_listsignups, arena);
            _commandManager.AddCommand("clearsignups", Command_clearsignups, arena);
            _commandManager.AddCommand("signup", Command_signup, arena);
            _commandManager.AddCommand("removesignup", Command_removesignup, arena);
            _commandManager.AddCommand("forcesignup", Command_signup, arena);
            _commandManager.AddCommand("forceremovesignup", Command_removesignup, arena);
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            _commandManager.RemoveCommand("signupshelp", Command_signupshelp, arena);
            _commandManager.RemoveCommand("signupsversion", Command_signupsversion, arena);
            _commandManager.RemoveCommand("addevent", Command_addevent, arena);
            _commandManager.RemoveCommand("delevent", Command_delevent, arena);
            _commandManager.RemoveCommand("chgevent", Command_chgevent, arena);
            _commandManager.RemoveCommand("listevents", Command_listevents, arena);
            _commandManager.RemoveCommand("startsignups", Command_startsignups, arena);
            _commandManager.RemoveCommand("endsignups", Command_endsignups, arena);
            _commandManager.RemoveCommand("listsignups", Command_listsignups, arena);
            _commandManager.RemoveCommand("signups", Command_listsignups, arena);
            _commandManager.RemoveCommand("clearsignups", Command_clearsignups, arena);
            _commandManager.RemoveCommand("signup", Command_signup, arena);
            _commandManager.RemoveCommand("removesignup", Command_removesignup, arena);
            _commandManager.RemoveCommand("forcesignup", Command_signup, arena);
            _commandManager.RemoveCommand("forceremovesignup", Command_removesignup, arena);
            return true;
        }

        #endregion

        #region ISignUps

        bool ISignUps.IsAvailable => _db.IsAvailable;

        async Task<SignUpMatch> ISignUps.IsPlayerSignedUpAsync(string eventName, string playerNamePrefix)
        {
            if (!_db.IsAvailable || string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(playerNamePrefix))
                return new SignUpMatch(-1, []);

            IReadOnlyList<string> names = await _db.FindSignUpsAsync(eventName, playerNamePrefix).ConfigureAwait(false);
            if (names.Count == 0)
                return new SignUpMatch(0, []);

            foreach (string name in names)
            {
                if (name.Equals(playerNamePrefix, StringComparison.OrdinalIgnoreCase))
                    return new SignUpMatch(1, [name]);
            }

            return new SignUpMatch(names.Count, names);
        }

        Task<bool> ISignUps.AddPlayerAsync(string eventName, string playerName)
        {
            if (!_db.IsAvailable || string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(playerName))
                return Task.FromResult(false);

            return _db.AddSignUpAsync(eventName, playerName);
        }

        Task<bool> ISignUps.RemovePlayerAsync(string eventName, string playerName)
        {
            if (!_db.IsAvailable || string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(playerName))
                return Task.FromResult(false);

            return _db.RemoveSignUpAsync(eventName, playerName);
        }

        async Task<bool> ISignUps.IsValidEventAsync(string eventName)
        {
            if (!_db.IsAvailable || string.IsNullOrEmpty(eventName))
                return false;

            return await _db.GetEventStateAsync(eventName).ConfigureAwait(false) != EventState.NotFound;
        }

        void ISignUps.PrintHelp(Player player) => PrintHelp(player);

        #endregion

        #region Commands

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays the sign-up commands.")]
        private void Command_signupshelp(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            PrintHelp(player);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays the sign-ups module version.")]
        private void Command_signupsversion(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _chat.SendMessage(player, "PowerBall Signups module (C# port of ASSS Signups Module v1.0 by POiD)");
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<event>:<description>", Description = "Adds a new sign-up event. (staff)")]
        private void Command_addevent(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!CheckDb(player))
                return;

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, "Please specify the event name and description. e.g. '?addevent Event1:First Event'");
                return;
            }

            SplitColon(arg, out ReadOnlySpan<char> eventName, out ReadOnlySpan<char> description);
            if (eventName.Length > 32)
            {
                _chat.SendMessage(player, "Event Name cannot be longer than 32 characters");
                return;
            }
            if (description.Length > 250)
            {
                _chat.SendMessage(player, "Event Description cannot be longer than 250 characters");
                return;
            }

            string ev = eventName.ToString();
            string desc = description.ToString();
            Run(player, async () =>
            {
                if (await _db.GetEventStateAsync(ev).ConfigureAwait(false) != EventState.NotFound)
                {
                    _chat.SendMessage(player, "Event already exists.");
                    return;
                }

                _chat.SendMessage(player, await _db.AddEventAsync(ev, desc).ConfigureAwait(false) ? "Event created!" : "Unable to create event.");
            });
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<event>", Description = "Deletes an event and its sign-ups. (staff)")]
        private void Command_delevent(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!CheckDb(player))
                return;

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, "Please specify the event name. e.g. '?delevent Event1'");
                return;
            }
            if (arg.Contains(':'))
            {
                _chat.SendMessage(player, "Event Name cannot contain ':' characters.");
                return;
            }
            if (arg.Length > 32)
            {
                _chat.SendMessage(player, "Event Name cannot be longer than 32 characters");
                return;
            }

            string ev = arg.ToString();
            Run(player, async () =>
                _chat.SendMessage(player, await _db.DeleteEventAsync(ev).ConfigureAwait(false) ? "Event deleted!" : "Unable to delete event."));
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<event>:<new description>", Description = "Changes an event's description. (staff)")]
        private void Command_chgevent(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!CheckDb(player))
                return;

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, "Please specify the event name and new description. e.g. '?chgevent Event1:New description here'");
                return;
            }

            SplitColon(arg, out ReadOnlySpan<char> eventName, out ReadOnlySpan<char> description);
            if (eventName.Length > 32)
            {
                _chat.SendMessage(player, "Event Name cannot be longer than 32 characters");
                return;
            }
            if (description.Length > 250)
            {
                _chat.SendMessage(player, "Event Description cannot be longer than 250 characters");
                return;
            }

            string ev = eventName.ToString();
            string desc = description.ToString();
            Run(player, async () =>
            {
                if (await _db.GetEventStateAsync(ev).ConfigureAwait(false) == EventState.NotFound)
                {
                    _chat.SendMessage(player, "Cannot find event.");
                    return;
                }

                _chat.SendMessage(player, await _db.ChangeEventDescriptionAsync(ev, desc).ConfigureAwait(false) ? "Event description updated!" : "Unable to change event.");
            });
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Lists current events and their descriptions.")]
        private void Command_listevents(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!CheckDb(player))
                return;

            Run(player, async () =>
            {
                IReadOnlyList<EventInfo> events = await _db.GetEventsAsync().ConfigureAwait(false);
                if (events.Count == 0)
                {
                    _chat.SendMessage(player, "There are currently no events.");
                    return;
                }

                _chat.SendMessage(player, "Event ID                         Status     Description");
                foreach (EventInfo e in events)
                    _chat.SendMessage(player, $"{e.Name,-32} {(e.Active ? "Open" : "Closed"),-8}   {e.Description}");
            });
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<event>", Description = "Allows sign-ups to start for an event. (staff)")]
        private void Command_startsignups(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            SetActive(player, parameters, true, "Please specify the event to start signups", "Event signups started.");
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<event>", Description = "Ends sign-ups for an event. (staff)")]
        private void Command_endsignups(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            SetActive(player, parameters, false, "Please specify the event to end signups", "Event signups ended.");
        }

        private void SetActive(Player player, ReadOnlySpan<char> parameters, bool active, string emptyMessage, string successMessage)
        {
            if (!CheckDb(player))
                return;

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, emptyMessage);
                return;
            }

            string ev = arg.ToString();
            Run(player, async () =>
                _chat.SendMessage(player, await _db.SetEventActiveAsync(ev, active).ConfigureAwait(false) ? successMessage : "Unable to update the event."));
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "[event]", Description = "Lists players signed up for an event (or the arena's active event).")]
        private void Command_listsignups(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!CheckDb(player))
                return;

            ReadOnlySpan<char> arg = parameters.Trim();
            string? ev = arg.IsEmpty ? GetActiveEvent(player.Arena) : arg.ToString();

            if (string.IsNullOrEmpty(ev))
            {
                if (!arg.IsEmpty)
                    return;
                _chat.SendMessage(player, "Please specify the event to list signups");
                return;
            }

            Run(player, async () =>
            {
                IReadOnlyList<string> names = await _db.GetSignUpsAsync(ev).ConfigureAwait(false);
                if (names.Count == 0)
                {
                    _chat.SendMessage(player, "There are currently no sign ups.");
                    return;
                }

                int i = 0;
                foreach (string name in names)
                    _chat.SendMessage(player, $"{++i,2}. {name,-32}");
            });
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<event>", Description = "Clears the sign-up list for an event. (staff)")]
        private void Command_clearsignups(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!CheckDb(player))
                return;

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, "Please specify the event to clear signups");
                return;
            }

            string ev = arg.ToString();
            Run(player, async () =>
            {
                await _db.ClearSignUpsAsync(ev).ConfigureAwait(false);
                _chat.SendMessage(player, "Sign Up List Cleared");
            });
        }

        [CommandHelp(Targets = CommandTarget.Any, Args = "<event> | <event>:<player>", Description = "Sign up for an event (staff can sign up others).")]
        private void Command_signup(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!CheckDb(player))
                return;

            bool isForce = _capabilityManager.HasCapability(player, "cmd_forcesignup");
            if (!TryResolveSelfOrTarget(player, parameters, target, isForce,
                "You do not have the authorization to add {0} to an event.",
                "Please specify the event you wish to sign up for.",
                out string ev, out string playerName))
            {
                return;
            }

            Run(player, async () =>
            {
                switch (await _db.GetEventStateAsync(ev).ConfigureAwait(false))
                {
                    case EventState.NotFound:
                        _chat.SendMessage(player, "Cannot find event.");
                        return;
                    case EventState.Inactive:
                        _chat.SendMessage(player, "Event is not currently accepting sign ups.");
                        return;
                }

                IReadOnlyList<string> existing = await _db.FindSignUpsAsync(ev, playerName).ConfigureAwait(false);
                foreach (string name in existing)
                {
                    if (name.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        _chat.SendMessage(player, "You are already signed up for the event.");
                        return;
                    }
                }

                _chat.SendMessage(player, await _db.AddSignUpAsync(ev, playerName).ConfigureAwait(false) ? "You have been added to the signup list!" : "Unable to perform signup.");
            });
        }

        [CommandHelp(Targets = CommandTarget.Any, Args = "<event> | <event>:<player>", Description = "Remove your sign-up for an event (staff can remove others).")]
        private void Command_removesignup(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!CheckDb(player))
                return;

            bool isForce = _capabilityManager.HasCapability(player, "cmd_forceremovesignup");
            if (!TryResolveSelfOrTarget(player, parameters, target, isForce,
                "You do not have the authorization to remove {0} from an event.",
                "Please specify the event you wish to be removed from.",
                out string ev, out string playerName))
            {
                return;
            }

            Run(player, async () =>
            {
                switch (await _db.GetEventStateAsync(ev).ConfigureAwait(false))
                {
                    case EventState.NotFound:
                        _chat.SendMessage(player, "Cannot find event.");
                        return;
                    case EventState.Inactive:
                        _chat.SendMessage(player, "Event is not currently active.");
                        return;
                }

                IReadOnlyList<string> existing = await _db.FindSignUpsAsync(ev, playerName).ConfigureAwait(false);
                if (existing.Count == 0)
                {
                    _chat.SendMessage(player, $"{playerName} is not currently signed up for the event.");
                    return;
                }

                await _db.RemoveSignUpAsync(ev, playerName).ConfigureAwait(false);
                _chat.SendMessage(player, $"{playerName} has been removed from the signup list for {ev}");
            });
        }

        #endregion

        #region Helpers

        private bool CheckDb(Player player)
        {
            if (_db.IsAvailable)
                return true;

            _chat.SendMessage(player, DbUnavailable);
            return false;
        }

        private bool TryResolveSelfOrTarget(Player player, ReadOnlySpan<char> parameters, ITarget target, bool isForce,
            string unauthorizedFormat, string emptyMessage, out string eventName, out string playerName)
        {
            eventName = "";
            playerName = "";

            if (target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                if (!isForce)
                {
                    _chat.SendMessage(player, string.Format(unauthorizedFormat, targetPlayer.Name));
                    return false;
                }

                eventName = parameters.Trim().ToString();
                playerName = targetPlayer.Name ?? "";
                return true;
            }

            ReadOnlySpan<char> arg = parameters.Trim();
            if (isForce && arg.Contains(':'))
            {
                SplitColon(arg, out ReadOnlySpan<char> ev, out ReadOnlySpan<char> pn);
                eventName = ev.ToString();
                playerName = pn.ToString();
                return true;
            }

            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, emptyMessage);
                return false;
            }

            eventName = arg.ToString();
            playerName = player.Name ?? "";
            return true;
        }

        private string? GetActiveEvent(Arena? arena)
        {
            if (arena is null)
                return null;

            ITeams? teams = arena.GetInterface<ITeams>();
            if (teams is null)
                return null;

            try { return teams.GetActiveEvent(arena); }
            finally { arena.ReleaseInterface(ref teams); }
        }

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

        private void Run(Player player, Func<Task> action)
        {
            _ = RunAsync(action);
        }

        private async Task RunAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(SignUps), $"Database error. {ex.Message}");
            }
        }

        private void PrintHelp(Player player)
        {
            _chat.SendMessage(player, "----------------------------------------------------");
            _chat.SendMessage(player, "The following PB Signups Module commands are available:");
            _chat.SendMessage(player, "----------------------------------------------------");
            DisplayCommand(player, "?listevents", "List current events and their description");
            DisplayCommand(player, "?signup <event>", "Add yourself to the signup list for <event>");
            DisplayCommand(player, "?removesignup <event>", "Remove yourself from signup list for <event>");
            DisplayCommand(player, "?listsignups [event]", "Lists players on the Signup list for [event] or the default arena event");
            DisplayCommand(player, "?signups [event]", "Lists players on the Signup list for [event] or the default arena event");

            bool displayedMod = false;
            DisplayModCommand(player, ref displayedMod, "addevent", "?addevent <event>:<desc>", "Add a new signup eligible event with a small description");
            DisplayModCommand(player, ref displayedMod, "delevent", "?delevent <event>", "Delete an existing event and all associated signups");
            DisplayModCommand(player, ref displayedMod, "chgevent", "?chgevent <event>:<new desc>", "Change the description on an existing event");
            DisplayModCommand(player, ref displayedMod, "startsignups", "?startsignups <event>", "Allow signups to start for <event>");
            DisplayModCommand(player, ref displayedMod, "endsignups", "?endsignups <event>", "End the signups for <event>");
            DisplayModCommand(player, ref displayedMod, "forcesignup", "?signup <event>:<player>", "Add <player> to the <event> signup list");
            DisplayModCommand(player, ref displayedMod, "forceremovesignup", "?removesignup <event>:<player>", "Remove <player> from the <event> signup");
            DisplayModCommand(player, ref displayedMod, "clearsignups", "?clearsignups <event>", "Clears the Signup list");
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
