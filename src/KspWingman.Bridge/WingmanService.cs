// KSP Wingman · src/KspWingman.Bridge/WingmanService.cs
// The kRPC service "Wingman": LMP's chat, players and connection, for an agent that plays through kRPC.
// Guide:   Meat and Potatoes/08-Bridge.md

using System;
using System.Collections.Generic;
using System.Linq;
using KRPC.Service;
using KRPC.Service.Attributes;
using LmpClient;
using LmpClient.Systems.Chat;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.Status;
using LmpClient.Systems.Warp;
using LmpCommon.Enums;

namespace KspWingman.Bridge
{
    /// <summary>
    /// kRPC finds this class in any loaded mod. kRPC itself sees KSP (vessels, flight, the camera); this adds what only
    /// LMP knows. Everything runs on KSP's main thread (kRPC calls procedures there), like LMP's own windows. Lines are
    /// tab-separated text, which every kRPC client can read.
    /// </summary>
    [KRPCService(Name = "Wingman", GameScene = GameScene.All)]
    public static class WingmanService
    {
        /// <summary>Longest chat line the agent may send (LMP has no limit; a runaway agent shouldn't flood the chat).</summary>
        public const int MaxChatLength = 300;

        /// <summary>At most one chat line this often, for the same reason.</summary>
        public static readonly TimeSpan ChatInterval = TimeSpan.FromSeconds(1);

        private static readonly ChatLog Log = new ChatLog();
        private static DateTime _lastSent = DateTime.MinValue;

        /// <summary>This game's player name in LMP.</summary>
        [KRPCProcedure]
        public static string PlayerName() => SettingsSystem.CurrentSettings.PlayerName ?? "";

        /// <summary>
        /// Kerbals free to fly a new vessel, or -1 without a game. With none, KSP's launch stops on a "No Control!" dialog
        /// that kRPC can't answer (seen in the rig: the crew were all in planes parked beside the runway).
        /// </summary>
        [KRPCProcedure]
        public static int AvailableCrew() => HighLogic.CurrentGame?.CrewRoster?.GetAvailableCrewCount() ?? -1;

        /// <summary>
        /// Closes KSP's "Mission Summary" window (shown after a recovery), which nobody can click from kRPC; with it open,
        /// the next launch waited forever in the rig. Returns how many it closed.
        /// </summary>
        [KRPCProcedure]
        public static int CloseDialogs()
        {
            var closed = 0;
            foreach (var dialog in UnityEngine.Object.FindObjectsOfType<KSP.UI.Screens.MissionRecoveryDialog>())
            {
                UnityEngine.Object.Destroy(dialog.gameObject);
                closed++;
            }
            return closed;
        }

        /// <summary>True while connected to a server and in the game.</summary>
        [KRPCProcedure]
        public static bool InGame() => MainSystem.NetworkState >= ClientState.Running;

        /// <summary>Every other player: "name \t vessel \t status" (LMP's status line, e.g. "Flying above Kerbin").</summary>
        [KRPCProcedure]
        public static IList<string> Players()
        {
            if (!InGame() || StatusSystem.Singleton == null)
                return new List<string>();
            var me = PlayerName();
            return StatusSystem.Singleton.PlayerStatusList.Values
                .Where(p => p != null && p.PlayerName != me)
                .Select(p => Clean(p.PlayerName) + "\t" + Clean(KSP.Localization.Localizer.Format(p.VesselText ?? "")) + "\t" + Clean(p.StatusText))
                .ToList();
        }

        /// <summary>Chat lines after number <paramref name="after"/> (0 for all kept): "id \t from \t text".</summary>
        [KRPCProcedure]
        public static IList<string> Chat(int after)
        {
            var messages = ChatSystem.Singleton?.ChatMessages;
            if (messages != null)
                Log.Update(messages.ToList(), m => m.Item1, m => m.Item2);
            return Log.After(after).Select(l => l.Id + "\t" + Clean(l.From) + "\t" + Clean(l.Text)).ToList();
        }

        /// <summary>
        /// Says something in LMP's chat, as this player. One line, at most <see cref="MaxChatLength"/> characters, at
        /// most one a second. Returns an empty string, or why it wasn't sent.
        /// </summary>
        [KRPCProcedure]
        public static string SendChat(string text)
        {
            text = Clean(text).Trim();
            if (text.Length == 0)
                return "Nothing to send.";
            if (text.Length > MaxChatLength)
                return $"Too long ({text.Length} characters; at most {MaxChatLength}).";
            if (!InGame() || ChatSystem.Singleton == null)
                return "Not in a game on a server.";
            if (DateTime.UtcNow - _lastSent < ChatInterval)
                return "Too soon: one line a second at most.";
            _lastSent = DateTime.UtcNow;
            ChatSystem.Singleton.MessageSender.SendChatMsg(text, true);
            return "";
        }

        /// <summary>How far (s) the player furthest ahead in time is ahead of this game (0 if none or warping).</summary>
        [KRPCProcedure]
        public static double SecondsBehind()
        {
            var warp = WarpSystem.Singleton;
            if (!InGame() || warp == null || warp.CurrentlyWarping)
                return 0;
            var me = PlayerName();
            var mine = warp.GetSubspaceTime(warp.CurrentSubspace);
            var others = warp.ClientSubspaceList.Where(p => p.Key != me && p.Value >= 0).Select(p => warp.GetSubspaceTime(p.Value)).ToList();
            return others.Count == 0 ? 0 : Math.Max(0, others.Max() - mine);
        }

        /// <summary>
        /// Where another player's vessel is, as LMP shows it in this game: { latitude, longitude, altitude (m above sea
        /// level) }, or empty if they have no vessel (or it isn't in this game). Their vessel is the one they hold LMP's
        /// control lock for.
        /// </summary>
        [KRPCProcedure]
        public static IList<double> PlayerPosition(string player)
        {
            if (!InGame() || string.IsNullOrEmpty(player))
                return new List<double>();
            foreach (var vessel in FlightGlobals.Vessels)
                if (vessel != null && LmpClient.Systems.Lock.LockSystem.LockQuery.GetControlLockOwner(vessel.id) == player)
                    return new List<double> { vessel.latitude, vessel.longitude, vessel.altitude };
            return new List<double>();
        }

        /// <summary>A stock name as KSP shows it: "#autoLOC_501174" → "Aeris 3A" (other text is returned as is).</summary>
        [KRPCProcedure]
        public static string Localize(string text) =>
            text != null && text.StartsWith("#autoLOC", StringComparison.Ordinal) ? KSP.Localization.Localizer.Format(text) : text ?? "";

        /// <summary>No tabs or line breaks inside a field (they separate fields and lines).</summary>
        private static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
