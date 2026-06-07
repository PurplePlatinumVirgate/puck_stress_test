using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PuckStressTest
{
    // Machine-readable run script. See playbooks/README.md for the
    // schema. Loaded once at startup; bots read from the parsed object
    // each tick to know what behaviors are on and what scripted actions
    // are due to fire.
    [Serializable]
    public class Playbook
    {
        public string Name = "";
        public string Description = "";
        public int SchemaVersion = 1;
        public float DurationSeconds = 30f;
        public int Seed = 1;
        public int BotCount = 1;
        public string TeamAssignment = "alternate";
        // Bot input tick rate in Hz. Defaults to 30 (conservative);
        // set to 200 to match Puck's `clientTickRate` for max load /
        // realism. Read by BotConfig and passed into BotBrain.
        public float InputTickHz = 30f;
        // Whether bots send /vs in chat during Warmup. Defaults OFF
        // so a bot run against community servers doesn't spam votes;
        // playbooks targeting our own dedicated test server set true.
        public bool VoteStart = false;
        // Whether bots send /vw outside Warmup phase to force Warmup
        // (mid-run cycle test). Pairs with VoteStart for repeatable
        // Play↔Warmup ping-pong. OFF for community-server safety.
        public bool VoteWarmup = false;
        // Delay after first Playing phase before /vw fires. Default
        // 30s lets us capture a clean Play baseline first.
        public float VoteWarmupAfterSeconds = 30f;
        public PositionDistribution PositionDistribution = new();
        public BehaviorToggles Behavior = new();
        public List<ScriptedAction> ScriptedActions = new();
        // Free-form opaque payload — each mod plugin reads its own slice.
        public Dictionary<string, JObject> ModSpecific = new();

        public static Playbook LoadOrDefault(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Debug.Log("[Playbook] no path given or file missing — using defaults");
                return new Playbook();
            }
            try
            {
                string json = File.ReadAllText(path);
                var settings = new JsonSerializerSettings
                {
                    ContractResolver = new SnakeCaseContractResolver(),
                    NullValueHandling = NullValueHandling.Ignore,
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                };
                var pb = JsonConvert.DeserializeObject<Playbook>(json, settings);
                Debug.Log(
                    $"[Playbook] loaded '{pb.Name}' " +
                    $"bots={pb.BotCount} duration={pb.DurationSeconds}s " +
                    $"actions={pb.ScriptedActions.Count}");
                return pb;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Playbook] failed to load '{path}': {ex.Message} — using defaults");
                return new Playbook();
            }
        }
    }

    [Serializable]
    public class PositionDistribution
    {
        public int Skater = 11;
        public int Goalie = 1;
    }

    [Serializable]
    public class BehaviorToggles
    {
        public bool SkateToPuck = true;
        public bool RotateStickToPuck = true;
        public bool RotateHeadToPuck = true;
        public bool AttemptPoke = true;
        public bool PushToOpposingGoal = true;
        public bool RespectFaceoff = true;
        public bool PassToTeammate = false;
        public bool LineChange = false;
    }

    [Serializable]
    public class ScriptedAction
    {
        public float AtSeconds;
        // "all" or a JArray of ints — handled in the resolver below.
        public JToken BotIndices = new JValue("all");
        public string Action = "";
        public JObject Args = new();

        public bool TargetsBot(int botIndex)
        {
            if (BotIndices == null) return true;
            if (BotIndices.Type == JTokenType.String &&
                string.Equals((string)BotIndices, "all", StringComparison.OrdinalIgnoreCase))
                return true;
            if (BotIndices is JArray arr)
            {
                foreach (var t in arr)
                {
                    if (t.Type == JTokenType.Integer && (int)t == botIndex) return true;
                }
            }
            return false;
        }
    }

    // Maps PascalCase C# property names to snake_case JSON keys, since
    // the playbook README uses snake_case throughout.
    internal class SnakeCaseContractResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
    {
        protected override string ResolvePropertyName(string propertyName)
        {
            // Cheap PascalCase → snake_case: lowercase + underscore before
            // each uppercase letter (except the first).
            if (string.IsNullOrEmpty(propertyName)) return propertyName;
            var sb = new System.Text.StringBuilder(propertyName.Length + 4);
            for (int i = 0; i < propertyName.Length; i++)
            {
                char c = propertyName[i];
                if (i > 0 && char.IsUpper(c)) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
