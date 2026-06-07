using System;
using System.Text;

namespace PuckStressTest
{
    // Mirror of Puck's ConnectionData (decompile: Puck_B323/ConnectionData.cs).
    // Sent ASCII-JSON-encoded as the NGO connection-approval payload.
    //
    // B323 schema (Puck_B323/Puck/ConnectionData.cs):
    //   SteamId, Key, Password, EnabledModIds (string[]), Handedness,
    //   FlagID + 22 cosmetic int IDs.
    //
    // B323 server flow (ConnectionApprovalManager.OnConnectionApproval-
    // Started, Puck_B323/Puck/ConnectionApprovalManager.cs:234):
    //   1. GetConnectionRejectionCode(): server-full/banned/whitelist/
    //      password-mismatch/missing-mods checks.
    //   2. WebSocketManager.Emit("serverConnectionApprovalRequest",
    //      {steamId, key}) → backend service.
    //   3. Backend responds with success+PlayerData; only then
    //      ApproveConnection(clientId, playerData) fires.
    //
    // Our testserver runs BotAuthBypassMod which Harmony-Prefixes step
    // 2/3: for SteamIds starting with "botsteam", it skips the WS emit
    // and calls ApproveConnection directly with fabricated PlayerData.
    // The bot's `Key` field can therefore be empty.
    //
    // Hand-rolled serializer because Unity's BCL does not ship
    // System.Text.Json and the schema is tiny. Field names and PascalCase
    // must match Puck's class so the server's JSON deserializer
    // accepts them.
    [Serializable]
    public class ConnectionData
    {
        public string SteamId = "";
        public string Key = "";              // B323 NEW. Backend auth ticket; empty when bypass mod handles approval.
        public string Password = "";
        public string[] EnabledModIds = Array.Empty<string>();  // B323: type changed ulong→string.
        // Handedness + 22 cosmetic int IDs default to 0 (None/no-asset).
        // Server replaces these with PlayerData from the connection-
        // approval backend (or our bypass fabrication), so values here
        // don't actually drive cosmetics — they just have to deserialize.
        public PlayerHandedness Handedness = 0;
        public int FlagID = 0;
        public int HeadgearIDBlueAttacker = 0;
        public int HeadgearIDRedAttacker = 0;
        public int HeadgearIDBlueGoalie = 0;
        public int HeadgearIDRedGoalie = 0;
        public int MustacheID = 0;
        public int BeardID = 0;
        public int JerseyIDBlueAttacker = 0;
        public int JerseyIDRedAttacker = 0;
        public int JerseyIDBlueGoalie = 0;
        public int JerseyIDRedGoalie = 0;
        public int StickSkinIDBlueAttacker = 0;
        public int StickSkinIDRedAttacker = 0;
        public int StickSkinIDBlueGoalie = 0;
        public int StickSkinIDRedGoalie = 0;
        public int StickShaftTapeIDBlueAttacker = 0;
        public int StickShaftTapeIDRedAttacker = 0;
        public int StickShaftTapeIDBlueGoalie = 0;
        public int StickShaftTapeIDRedGoalie = 0;
        public int StickBladeTapeIDBlueAttacker = 0;
        public int StickBladeTapeIDRedAttacker = 0;
        public int StickBladeTapeIDBlueGoalie = 0;
        public int StickBladeTapeIDRedGoalie = 0;

        // PlayerHandedness mirrors PuckStressTest.Mirror.PlayerHandedness;
        // declared locally so ConnectionData doesn't pull the Mirror
        // namespace into the BotHost root.
        public enum PlayerHandedness { None = 0, Left = 1, Right = 2 }

        public string ToJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendString(sb, "SteamId",  SteamId);   sb.Append(',');
            AppendString(sb, "Key",      Key);       sb.Append(',');
            AppendString(sb, "Password", Password);  sb.Append(',');
            AppendStringArray(sb, "EnabledModIds", EnabledModIds); sb.Append(',');
            AppendInt(sb, "Handedness", (int)Handedness); sb.Append(',');
            AppendInt(sb, "FlagID", FlagID); sb.Append(',');
            AppendInt(sb, "HeadgearIDBlueAttacker", HeadgearIDBlueAttacker); sb.Append(',');
            AppendInt(sb, "HeadgearIDRedAttacker", HeadgearIDRedAttacker); sb.Append(',');
            AppendInt(sb, "HeadgearIDBlueGoalie", HeadgearIDBlueGoalie); sb.Append(',');
            AppendInt(sb, "HeadgearIDRedGoalie", HeadgearIDRedGoalie); sb.Append(',');
            AppendInt(sb, "MustacheID", MustacheID); sb.Append(',');
            AppendInt(sb, "BeardID", BeardID); sb.Append(',');
            AppendInt(sb, "JerseyIDBlueAttacker", JerseyIDBlueAttacker); sb.Append(',');
            AppendInt(sb, "JerseyIDRedAttacker", JerseyIDRedAttacker); sb.Append(',');
            AppendInt(sb, "JerseyIDBlueGoalie", JerseyIDBlueGoalie); sb.Append(',');
            AppendInt(sb, "JerseyIDRedGoalie", JerseyIDRedGoalie); sb.Append(',');
            AppendInt(sb, "StickSkinIDBlueAttacker", StickSkinIDBlueAttacker); sb.Append(',');
            AppendInt(sb, "StickSkinIDRedAttacker", StickSkinIDRedAttacker); sb.Append(',');
            AppendInt(sb, "StickSkinIDBlueGoalie", StickSkinIDBlueGoalie); sb.Append(',');
            AppendInt(sb, "StickSkinIDRedGoalie", StickSkinIDRedGoalie); sb.Append(',');
            AppendInt(sb, "StickShaftTapeIDBlueAttacker", StickShaftTapeIDBlueAttacker); sb.Append(',');
            AppendInt(sb, "StickShaftTapeIDRedAttacker", StickShaftTapeIDRedAttacker); sb.Append(',');
            AppendInt(sb, "StickShaftTapeIDBlueGoalie", StickShaftTapeIDBlueGoalie); sb.Append(',');
            AppendInt(sb, "StickShaftTapeIDRedGoalie", StickShaftTapeIDRedGoalie); sb.Append(',');
            AppendInt(sb, "StickBladeTapeIDBlueAttacker", StickBladeTapeIDBlueAttacker); sb.Append(',');
            AppendInt(sb, "StickBladeTapeIDRedAttacker", StickBladeTapeIDRedAttacker); sb.Append(',');
            AppendInt(sb, "StickBladeTapeIDBlueGoalie", StickBladeTapeIDBlueGoalie); sb.Append(',');
            AppendInt(sb, "StickBladeTapeIDRedGoalie", StickBladeTapeIDRedGoalie);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendString(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":\"");
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void AppendInt(StringBuilder sb, string key, int value)
        {
            sb.Append('"').Append(key).Append("\":").Append(value);
        }

        private static void AppendStringArray(StringBuilder sb, string key, string[] values)
        {
            sb.Append('"').Append(key).Append("\":[");
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendStringLiteral(sb, values[i]);
                }
            }
            sb.Append(']');
        }

        private static void AppendStringLiteral(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
