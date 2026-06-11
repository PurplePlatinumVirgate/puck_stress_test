using System;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BotAuthBypassMod
{
    // Server-side Puck plugin that short-circuits B323's new backend-
    // mediated connection-approval flow for bot clients. Loaded ONLY on
    // the local testserver — never ship to production.
    //
    // Background (Puck_B323/Puck/ConnectionApprovalManager.cs:234-257):
    //   When a client connects, ConnectionApprovalManager.OnConnection-
    //   ApprovalStarted runs basic pre-flight checks (server-full,
    //   banned, password, missing mods), then calls
    //     WebSocketManager.Emit("serverConnectionApprovalRequest",
    //                           {steamId, key})
    //   and waits for the backend service to respond with a
    //   ServerConnectionApprovalResponse containing PlayerData. Only
    //   then does ApproveConnection(clientId, playerData) fire.
    //
    //   Our bots use fake SteamIds like "botsteam0000_42" with empty
    //   Key. The backend service rejects every one, so without this
    //   mod no bot can connect.
    //
    // What this mod does:
    //   Harmony-Prefix OnConnectionApprovalStarted. For SteamIds that
    //   match the bot pattern, skip the WS emit and call
    //   ApproveConnection directly with fabricated PlayerData. Real
    //   clients (non-bot SteamIds) fall through to the original method,
    //   which performs the WS handshake unchanged.
    //
    // Why a Prefix and not a Postfix:
    //   The original method ends with WebSocketManager.Emit, which is
    //   fire-and-forget (it parks the approval until the response).
    //   Postfixing wouldn't undo that — we need to skip the original
    //   entirely and substitute our own approval call.
    public class Plugin : IPuckPlugin
    {
        public const string Name = "BotAuthBypassMod";
        public const string Guid = "com.puckstresstest.botauthbypass";

        // Bot SteamIds always start with this prefix. Matches the
        // pattern emitted by BotHost/BotInstance.Connect (line ~57).
        // Real Puck SteamIds are pure decimal digits (Steam 64-bit IDs)
        // so the literal "botsteam" prefix can never collide.
        private const string BotSteamIdPrefix = "botsteam";

        private static readonly Harmony s_harmony = new Harmony(Guid);

        public bool OnEnable()
        {
            try
            {
                s_harmony.PatchAll();
                Log("Enabled — Harmony prefix applied on ConnectionApprovalManager.OnConnectionApprovalStarted; bot SteamIds (prefix '" + BotSteamIdPrefix + "') will bypass the backend WS auth.");
                return true;
            }
            catch (Exception ex)
            {
                LogError("Failed to enable: " + ex);
                return false;
            }
        }

        public bool OnDisable()
        {
            try
            {
                s_harmony.UnpatchSelf();
                Log("Disabled — bypass removed; backend auth required for all clients.");
                return true;
            }
            catch (Exception ex)
            {
                LogError("Failed to disable: " + ex);
                return false;
            }
        }

        internal static bool IsBotSteamId(string steamId)
            => !string.IsNullOrEmpty(steamId) && steamId.StartsWith(BotSteamIdPrefix, StringComparison.Ordinal);

        internal static PlayerData FabricatePlayerData(string steamId, ConnectionApproval approval)
        {
            // Pick name + jersey randomly but deterministically per
            // SteamId — so a bot reconnecting keeps its identity, but
            // the launch-order of bot processes doesn't produce a
            // monotonic Adrian/Albert/Allen/... + 1/2/3/... pattern
            // on the scoreboard. Seeded by steamId.GetHashCode() XORed
            // with a constant to keep the seed away from .NET's default
            // string-hash distribution. Rare duplicates across the 12
            // bots are possible but visually fine.
            int seed = (steamId ?? "").GetHashCode() ^ 0x6A4C9F11;
            var rng = new System.Random(seed);
            string botName = "Bot " + s_BotNames[rng.Next(s_BotNames.Length)];
            int jerseyNumber = rng.Next(1, 100); // 1..99 inclusive

            return new PlayerData
            {
                steamId = steamId,
                username = botName,
                number = jerseyNumber,
                usernameChangedAt = null,
                patreonLevel = 0,
                mmr = 0,
                adminLevel = 0,
                items = Array.Empty<PlayerItem>(),
                mutes = Array.Empty<PlayerMute>(),
                bans = Array.Empty<PlayerBan>(),
                cooldowns = Array.Empty<PlayerCooldown>(),
            };
        }

        // Counter-Strike bot name pool (Valve's bot_names.txt list, ships
        // with CS:GO/CS2). Lives here because B323 moved username off the
        // Subscription RPC and onto PlayerData from the auth backend —
        // which we fabricate server-side. Was previously in the bot-side
        // MirrorPlayer.SendPlayerSubscription before that RPC was removed.
        private static readonly string[] s_BotNames = {
            "Adrian","Albert","Allen","Alfred","Andrew","Anthony","Arnold","Arthur",
            "Barry","Bert","Brad","Bill","Brandon","Brian","Carl","Cecil","Chad",
            "Chris","Clarence","Clifford","Clyde","Cory","Craig","Dan","Daniel",
            "Dave","Dennis","Derek","Donald","Doug","Earl","Eddie","Edgar","Edward",
            "Elliot","Eric","Ernest","Eugene","Felix","Floyd","Francis","Frank",
            "Fred","Gary","Gene","George","Gerald","Glen","Gordon","Greg","Harold",
            "Harry","Henry","Howard","Hugh","Ivan","Jack","Jacob","James","Jason",
            "Jeff","Jerome","Jerry","Jim","Joe","John","Johnny","Jose","Joseph",
            "Juan","Keith","Ken","Kenneth","Kevin","Kirk","Lance","Larry","Lawrence",
            "Lee","Leo","Leon","Leonard","Leroy","Lester","Lewis","Lloyd","Louis",
            "Manuel","Marc","Mark","Martin","Matt","Maurice","Melvin","Michael",
            "Mike","Milton","Morris","Nathan","Nelson","Nicholas","Norman","Oscar",
            "Patrick","Paul","Peter","Philip","Quinn","Ralph","Randy","Ray","Raymond",
            "Rick","Robert","Rodney","Roger","Roland","Ronald","Roy","Russell","Sam",
            "Samuel","Scott","Sean","Stanley","Stephen","Steve","Steven","Stuart",
            "Ted","Terry","Thomas","Tim","Timothy","Todd","Tom","Tony","Travis",
            "Troy","Vernon","Victor","Vincent","Wallace","Walter","Warren","Wayne",
            "Wendell","Wesley","William","Willie",
        };

        private static string BotName(int idx)
            => s_BotNames[((idx % s_BotNames.Length) + s_BotNames.Length) % s_BotNames.Length];

        internal static void Log(string msg)      => Debug.Log("[" + Name + "] " + msg);
        internal static void LogError(string msg) => Debug.LogError("[" + Name + "] " + msg);
    }

    [HarmonyPatch(typeof(ConnectionApprovalManager), "OnConnectionApprovalStarted")]
    internal static class Patch_OnConnectionApprovalStarted
    {
        [HarmonyPrefix]
        private static bool Prefix(ConnectionApprovalManager __instance, ulong clientId, ConnectionApproval connectionApproval)
        {
            try
            {
                string steamId = connectionApproval?.ConnectionData?.SteamId;
                if (!Plugin.IsBotSteamId(steamId)) return true;  // real user — original handles backend auth

                // Run the same pre-flight checks the original does
                // (server-full, banned, password, missing mods), but with one
                // exception for bots: IGNORE the missing-mods rejection.
                //
                // Bots are headless stubs. They never carry the server's
                // client-required workshop mods and don't need them (they
                // render nothing). If we enforced the mod check, the bot would
                // be kicked, then reconnect on a rebuilt NetworkManager — and
                // that rebuild loses prefab-spawn capability, so the server's
                // Player/Puck spawns fail and the bot never gets a body to
                // claim a position with. Letting mod-less bots through on the
                // FIRST connect keeps them on the healthy NetworkManager#0
                // where scene-sync and spawns work. Genuine rejections
                // (server full, banned, bad password) are still honoured.
                //
                // Compare against the enum MEMBER (not a literal) so this stays
                // correct across Puck builds where MissingMods's numeric value
                // differs (7 on B897, 8 on older builds).
                ConnectionRejectionCode? code = __instance.GetConnectionRejectionCode(connectionApproval);
                if (code.HasValue && code.Value != ConnectionRejectionCode.MissingMods)
                {
                    __instance.RejectConnection(clientId, code.Value);
                    Plugin.Log($"Bot client {clientId} steamId={steamId} rejected pre-flight: {code.Value}");
                    return false;
                }
                if (code.HasValue)
                    Plugin.Log($"Bot client {clientId} steamId={steamId} missing mods ({code.Value}) — ignored for bot.");

                // Halt the NGO approval pipeline so we control the
                // resolution (the original also Halts before emitting
                // to the WS — same dance, different terminator).
                connectionApproval.Halt();
                var pd = Plugin.FabricatePlayerData(steamId, connectionApproval);
                __instance.ApproveConnection(clientId, pd);
                Plugin.Log($"Bot client {clientId} steamId={steamId} approved via bypass (no backend WS).");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Prefix on OnConnectionApprovalStarted failed: {ex}");
                return true;  // fall through to original on error so non-bots still work
            }
        }
    }
}
