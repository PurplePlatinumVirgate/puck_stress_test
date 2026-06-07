namespace PuckStressTest.Brain
{
    // Per-tick policy interface. Two implementations:
    //   - HeuristicBrain: wraps the existing BotBrain hand-coded
    //     decision logic. Today's default.
    //   - OnnxBrain (M5): loads a trained policy from disk, runs
    //     ONNX inference each tick, decodes to RPC args.
    //
    // Both implementations write to the same chokepoint
    // (MirrorPlayerInput.Send*) and read from the same one
    // (MirrorSynchronizedObjectManager.LatestPositions + Mirror NVs),
    // so the SnapshotLogger can sit between them and capture
    // (obs, action) tuples regardless of which brain is driving.
    public interface IBrain
    {
        // True once the brain has the world state it needs to decide.
        // Heuristic returns true once Player + body are spawned;
        // OnnxBrain additionally requires the model to be loaded.
        bool IsReady { get; }

        // Compute and emit one tick of inputs. Implementations are
        // responsible for calling Send* on the bot's MirrorPlayerInput.
        // Called from BotBrain.Update at the configured TickHz.
        void Tick();
    }
}
