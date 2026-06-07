// Local copies of Puck game enums + INetworkSerializable structs,
// transcribed from the decompile to avoid a Puck.dll dependency.
// Values, order, and underlying type (default int) MUST match the
// originals byte-for-byte — NGO serializes enum NetworkVariables as
// their raw integer value, and serializes structs via their
// declared NetworkSerialize.
//
// Source: the decompiled Puck assembly (build B323), Puck/
//   PlayerPhase.cs, PlayerHandedness.cs, PlayerTeam.cs, PlayerRole.cs,
//   GamePhase.cs, GameState.cs, PlayerGameState.cs,
//   PlayerCustomizationState.cs

namespace PuckStressTest.Mirror
{
    // B323 renamed Puck's `PlayerState` → `PlayerPhase` and collapsed
    // PositionSelectBlue / PositionSelectRed into one `PositionSelect`
    // value. We keep the C# identifier `PlayerState` (it's our enum
    // name; doesn't matter on the wire) but update ordinals to match
    // PlayerPhase.cs exactly. Old code referencing PositionSelectBlue/
    // PositionSelectRed must move to PositionSelect.
    public enum PlayerState
    {
        None = 0,
        TeamSelect = 1,
        PositionSelect = 2,
        Play = 3,
        Replay = 4,
        Spectate = 5,
    }

    // B323 PlayerHandedness.cs prepended `None` at 0, shifting Left/Right.
    public enum PlayerHandedness
    {
        None = 0,
        Left = 1,
        Right = 2,
    }

    // B323 PlayerTeam.cs reordered: Blue and Red swapped slots; Spectator
    // moved to end. Was (None=0, Spectator=1, Blue=2, Red=3).
    public enum PlayerTeam
    {
        None = 0,
        Blue = 1,
        Red = 2,
        Spectator = 3,
    }

    public enum PlayerRole
    {
        None = 0,
        Attacker = 1,
        Goalie = 2,
    }

    // GamePhase ordinals unchanged from B202 per B323 GamePhase.cs.
    public enum GamePhase
    {
        None = 0,
        Warmup = 1,
        FaceOff = 2,
        Playing = 3,
        BlueScore = 4,
        RedScore = 5,
        Replay = 6,
        PeriodOver = 7,
        GameOver = 8,
    }

    // Mirror of Puck's GameState struct (decompile: GameState.cs).
    // B323 changes: `Time` (int) → `Tick` (int) rename + added trailing
    // `IsOvertime` (bool). Same wire size for the first 5 fields, plus
    // one extra bool at the end.
    public struct GameState : Unity.Netcode.INetworkSerializable, System.IEquatable<GameState>
    {
        public GamePhase Phase;
        public int Tick;
        public int Period;
        public int BlueScore;
        public int RedScore;
        public bool IsOvertime;

        // Legacy alias so older bot code that read `Time` keeps working.
        public int Time => Tick;

        public void NetworkSerialize<T>(Unity.Netcode.BufferSerializer<T> serializer)
            where T : Unity.Netcode.IReaderWriter
        {
            if (serializer.IsReader)
            {
                var r = serializer.GetFastBufferReader();
                r.ReadValueSafe<GamePhase>(out Phase, default);
                r.ReadValueSafe<int>(out Tick, default);
                r.ReadValueSafe<int>(out Period, default);
                r.ReadValueSafe<int>(out BlueScore, default);
                r.ReadValueSafe<int>(out RedScore, default);
                r.ReadValueSafe<bool>(out IsOvertime, default);
            }
            else
            {
                var w = serializer.GetFastBufferWriter();
                w.WriteValueSafe<GamePhase>(Phase, default);
                w.WriteValueSafe<int>(Tick, default);
                w.WriteValueSafe<int>(Period, default);
                w.WriteValueSafe<int>(BlueScore, default);
                w.WriteValueSafe<int>(RedScore, default);
                w.WriteValueSafe<bool>(IsOvertime, default);
            }
        }

        public bool Equals(GameState other) =>
            Phase == other.Phase && Tick == other.Tick && Period == other.Period &&
            BlueScore == other.BlueScore && RedScore == other.RedScore &&
            IsOvertime == other.IsOvertime;
    }

    // NEW in B323: Player's `State`/`Team`/`Role` triplet collapsed into
    // one NetworkVariable<PlayerGameState>. Server-authoritative; bot
    // reads via MirrorPlayer.GameState.Value.{Phase,Team,Role}.
    // Source: PlayerGameState.cs:4-43.
    public struct PlayerGameState : Unity.Netcode.INetworkSerializable, System.IEquatable<PlayerGameState>
    {
        public PlayerState Phase;
        public PlayerTeam Team;
        public PlayerRole Role;

        public void NetworkSerialize<T>(Unity.Netcode.BufferSerializer<T> serializer)
            where T : Unity.Netcode.IReaderWriter
        {
            if (serializer.IsReader)
            {
                var r = serializer.GetFastBufferReader();
                r.ReadValueSafe<PlayerState>(out Phase, default);
                r.ReadValueSafe<PlayerTeam>(out Team, default);
                r.ReadValueSafe<PlayerRole>(out Role, default);
            }
            else
            {
                var w = serializer.GetFastBufferWriter();
                w.WriteValueSafe<PlayerState>(Phase, default);
                w.WriteValueSafe<PlayerTeam>(Team, default);
                w.WriteValueSafe<PlayerRole>(Role, default);
            }
        }

        public bool Equals(PlayerGameState other) =>
            Phase == other.Phase && Team == other.Team && Role == other.Role;
    }

    // NEW in B323: 23 cosmetic FixedString32Bytes NVs collapsed into one
    // NetworkVariable<PlayerCustomizationState> carrying 22 int asset
    // IDs. Bot doesn't care about cosmetic content — we just need the
    // struct layout for wire alignment. All 22 fields are ints; serialize
    // in declaration order.
    // Source: PlayerCustomizationState.cs:4-123.
    public struct PlayerCustomizationState : Unity.Netcode.INetworkSerializable, System.IEquatable<PlayerCustomizationState>
    {
        public int FlagID;
        public int HeadgearIDBlueAttacker;
        public int HeadgearIDRedAttacker;
        public int HeadgearIDBlueGoalie;
        public int HeadgearIDRedGoalie;
        public int MustacheID;
        public int BeardID;
        public int JerseyIDBlueAttacker;
        public int JerseyIDRedAttacker;
        public int JerseyIDBlueGoalie;
        public int JerseyIDRedGoalie;
        public int StickSkinIDBlueAttacker;
        public int StickSkinIDRedAttacker;
        public int StickSkinIDBlueGoalie;
        public int StickSkinIDRedGoalie;
        public int StickShaftTapeIDBlueAttacker;
        public int StickShaftTapeIDRedAttacker;
        public int StickShaftTapeIDBlueGoalie;
        public int StickShaftTapeIDRedGoalie;
        public int StickBladeTapeIDBlueAttacker;
        public int StickBladeTapeIDRedAttacker;
        public int StickBladeTapeIDBlueGoalie;
        public int StickBladeTapeIDRedGoalie;

        public void NetworkSerialize<T>(Unity.Netcode.BufferSerializer<T> serializer)
            where T : Unity.Netcode.IReaderWriter
        {
            if (serializer.IsReader)
            {
                var r = serializer.GetFastBufferReader();
                r.ReadValueSafe<int>(out FlagID, default);
                r.ReadValueSafe<int>(out HeadgearIDBlueAttacker, default);
                r.ReadValueSafe<int>(out HeadgearIDRedAttacker, default);
                r.ReadValueSafe<int>(out HeadgearIDBlueGoalie, default);
                r.ReadValueSafe<int>(out HeadgearIDRedGoalie, default);
                r.ReadValueSafe<int>(out MustacheID, default);
                r.ReadValueSafe<int>(out BeardID, default);
                r.ReadValueSafe<int>(out JerseyIDBlueAttacker, default);
                r.ReadValueSafe<int>(out JerseyIDRedAttacker, default);
                r.ReadValueSafe<int>(out JerseyIDBlueGoalie, default);
                r.ReadValueSafe<int>(out JerseyIDRedGoalie, default);
                r.ReadValueSafe<int>(out StickSkinIDBlueAttacker, default);
                r.ReadValueSafe<int>(out StickSkinIDRedAttacker, default);
                r.ReadValueSafe<int>(out StickSkinIDBlueGoalie, default);
                r.ReadValueSafe<int>(out StickSkinIDRedGoalie, default);
                r.ReadValueSafe<int>(out StickShaftTapeIDBlueAttacker, default);
                r.ReadValueSafe<int>(out StickShaftTapeIDRedAttacker, default);
                r.ReadValueSafe<int>(out StickShaftTapeIDBlueGoalie, default);
                r.ReadValueSafe<int>(out StickShaftTapeIDRedGoalie, default);
                r.ReadValueSafe<int>(out StickBladeTapeIDBlueAttacker, default);
                r.ReadValueSafe<int>(out StickBladeTapeIDRedAttacker, default);
                r.ReadValueSafe<int>(out StickBladeTapeIDBlueGoalie, default);
                r.ReadValueSafe<int>(out StickBladeTapeIDRedGoalie, default);
            }
            else
            {
                var w = serializer.GetFastBufferWriter();
                w.WriteValueSafe<int>(FlagID, default);
                w.WriteValueSafe<int>(HeadgearIDBlueAttacker, default);
                w.WriteValueSafe<int>(HeadgearIDRedAttacker, default);
                w.WriteValueSafe<int>(HeadgearIDBlueGoalie, default);
                w.WriteValueSafe<int>(HeadgearIDRedGoalie, default);
                w.WriteValueSafe<int>(MustacheID, default);
                w.WriteValueSafe<int>(BeardID, default);
                w.WriteValueSafe<int>(JerseyIDBlueAttacker, default);
                w.WriteValueSafe<int>(JerseyIDRedAttacker, default);
                w.WriteValueSafe<int>(JerseyIDBlueGoalie, default);
                w.WriteValueSafe<int>(JerseyIDRedGoalie, default);
                w.WriteValueSafe<int>(StickSkinIDBlueAttacker, default);
                w.WriteValueSafe<int>(StickSkinIDRedAttacker, default);
                w.WriteValueSafe<int>(StickSkinIDBlueGoalie, default);
                w.WriteValueSafe<int>(StickSkinIDRedGoalie, default);
                w.WriteValueSafe<int>(StickShaftTapeIDBlueAttacker, default);
                w.WriteValueSafe<int>(StickShaftTapeIDRedAttacker, default);
                w.WriteValueSafe<int>(StickShaftTapeIDBlueGoalie, default);
                w.WriteValueSafe<int>(StickShaftTapeIDRedGoalie, default);
                w.WriteValueSafe<int>(StickBladeTapeIDBlueAttacker, default);
                w.WriteValueSafe<int>(StickBladeTapeIDRedAttacker, default);
                w.WriteValueSafe<int>(StickBladeTapeIDBlueGoalie, default);
                w.WriteValueSafe<int>(StickBladeTapeIDRedGoalie, default);
            }
        }

        public bool Equals(PlayerCustomizationState o) =>
            FlagID == o.FlagID && HeadgearIDBlueAttacker == o.HeadgearIDBlueAttacker &&
            HeadgearIDRedAttacker == o.HeadgearIDRedAttacker && HeadgearIDBlueGoalie == o.HeadgearIDBlueGoalie &&
            HeadgearIDRedGoalie == o.HeadgearIDRedGoalie && MustacheID == o.MustacheID && BeardID == o.BeardID &&
            JerseyIDBlueAttacker == o.JerseyIDBlueAttacker && JerseyIDRedAttacker == o.JerseyIDRedAttacker &&
            JerseyIDBlueGoalie == o.JerseyIDBlueGoalie && JerseyIDRedGoalie == o.JerseyIDRedGoalie &&
            StickSkinIDBlueAttacker == o.StickSkinIDBlueAttacker && StickSkinIDRedAttacker == o.StickSkinIDRedAttacker &&
            StickSkinIDBlueGoalie == o.StickSkinIDBlueGoalie && StickSkinIDRedGoalie == o.StickSkinIDRedGoalie &&
            StickShaftTapeIDBlueAttacker == o.StickShaftTapeIDBlueAttacker &&
            StickShaftTapeIDRedAttacker == o.StickShaftTapeIDRedAttacker &&
            StickShaftTapeIDBlueGoalie == o.StickShaftTapeIDBlueGoalie &&
            StickShaftTapeIDRedGoalie == o.StickShaftTapeIDRedGoalie &&
            StickBladeTapeIDBlueAttacker == o.StickBladeTapeIDBlueAttacker &&
            StickBladeTapeIDRedAttacker == o.StickBladeTapeIDRedAttacker &&
            StickBladeTapeIDBlueGoalie == o.StickBladeTapeIDBlueGoalie &&
            StickBladeTapeIDRedGoalie == o.StickBladeTapeIDRedGoalie;
    }
}
