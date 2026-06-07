using System.Collections.Generic;
using UnityEngine;

namespace PuckStressTest
{
    // Walks the playbook's ScriptedActions list in time order and
    // dispatches each action to the matching bot(s) when their
    // `at_seconds` mark elapses.
    //
    // The actual send code (`SendChat`, `SendCommand`, `SendQuickChat`)
    // is stubbed today — it logs only — because the bots can't yet
    // hold a connection long enough to send these RPCs (see task #16/#17).
    // When that lands, swap the bodies of the Send* methods to real
    // RPC calls on the bot's Player NetworkObject.
    public class ScriptedActionsRunner : MonoBehaviour
    {
        public BotInstance Bot;
        public int BotIndex;

        private Playbook _pb;
        private float _startTime;
        private readonly HashSet<int> _fired = new();

        public void Init(Playbook pb, int botIndex, BotInstance bot)
        {
            _pb = pb;
            BotIndex = botIndex;
            Bot = bot;
            _startTime = Time.realtimeSinceStartup;
        }

        private void Update()
        {
            if (_pb == null) return;
            float t = Time.realtimeSinceStartup - _startTime;
            for (int i = 0; i < _pb.ScriptedActions.Count; i++)
            {
                if (_fired.Contains(i)) continue;
                var a = _pb.ScriptedActions[i];
                if (a.AtSeconds > t) continue;
                if (!a.TargetsBot(BotIndex)) { _fired.Add(i); continue; }

                Dispatch(a);
                _fired.Add(i);
            }
        }

        private void Dispatch(ScriptedAction a)
        {
            switch (a.Action)
            {
                case "chat":
                    SendChat(StringArg(a, "text"));
                    break;
                case "command":
                    SendCommand(StringArg(a, "command"));
                    break;
                case "quick_chat":
                    SendQuickChat(IntArg(a, "id"));
                    break;
                case "team_change":
                    SendTeamChange(StringArg(a, "team"));
                    break;
                case "position_change":
                    SendPositionChange(StringArg(a, "position"));
                    break;
                default:
                    Debug.LogWarning($"[Bot {BotIndex:D2}] unknown action '{a.Action}', skipping");
                    break;
            }
        }

        // Stubs — swap with real RPCs when the bot can hold a connection
        // and we know the matching Server_*Rpc method ids on Player.cs.
        // Notes-of-record:
        //   chat       → ServerManagerController has chat RPCs; method
        //                names TBD when we instrument task #5 fully.
        //   command    → ServerManagerController has command-string RPCs.
        //   quick_chat → likely a small enum on Player.cs.
        //   team/position changes go through Player.Server_*Rpc.

        private void SendChat(string text)
            => Debug.Log($"[Bot {BotIndex:D2}] CHAT: \"{text}\"");

        private void SendCommand(string cmd)
            => Debug.Log($"[Bot {BotIndex:D2}] COMMAND: {cmd}");

        private void SendQuickChat(int id)
            => Debug.Log($"[Bot {BotIndex:D2}] QUICK_CHAT: id={id}");

        private void SendTeamChange(string team)
            => Debug.Log($"[Bot {BotIndex:D2}] TEAM_CHANGE: {team}");

        private void SendPositionChange(string position)
            => Debug.Log($"[Bot {BotIndex:D2}] POSITION_CHANGE: {position}");

        private static string StringArg(ScriptedAction a, string key)
            => a.Args != null && a.Args.TryGetValue(key, out var t) ? (string)t : "";

        private static int IntArg(ScriptedAction a, string key)
            => a.Args != null && a.Args.TryGetValue(key, out var t) ? (int)t : 0;
    }
}
