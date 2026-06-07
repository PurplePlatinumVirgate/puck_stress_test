# Puck client → server input RPCs (build B202)

Discovered from the decompile. The Puck client sends ~20 input RPCs per
tick at 200 Hz (`PlayerInput.cs:175,2158`, configurable via
`ServerConfiguration.clientTickRate`). Each RPC fires ONLY on change
detection — `Client_MoveInputRpc` only sends when `MoveInput.HasChanged`
is true — so a stationary bot sends nothing for that field tick after
tick.

## RPC table

> NB: the agent that produced this had a couple of method-ID transcription
> bugs (DashLeft / DashRight / TwistRight all collided onto `341272022U`).
> Treat the method-ID column as a starting point — confirm by re-reading
> `PlayerInput.cs:1533–1562` (the registration block) when implementing
> the bot's send code.

| RPC | Signature | Delivery | Semantics |
|---|---|---|---|
| `Client_MoveInputRpc` | short x, short y | Reliable | Movement axes (bitpacked, x/y in -1..1 quantized to short) |
| `Client_RaycastOriginAngleInputRpc` | short x, short y | Unreliable | Stick raycast origin angle (degrees * 32767 / 360) |
| `Client_LookAngleInputRpc` | short yaw, short pitch | Unreliable | Head look angle (degrees * 32767 / 360) |
| `Client_BladeAngleInputRpc` | sbyte | Reliable | Stick blade twist (-127..127 degrees) |
| `Client_SlideInputRpc` | bool | Reliable | Slide button held |
| `Client_SprintInputRpc` | bool | Reliable | Sprint button held |
| `Client_TrackInputRpc` | bool | Reliable | Track / lock-on button held |
| `Client_LookInputRpc` | bool | Reliable | Look-toggle held |
| `Client_StopInputRpc` | bool | Reliable | Stop button held |
| `Client_JumpInputRpc` | () | Reliable | Jump impulse (counter increment) |
| `Client_TwistLeftInputRpc` | () | Reliable | Twist-left impulse |
| `Client_TwistRightInputRpc` | () | Reliable | Twist-right impulse |
| `Client_DashLeftInputRpc` | () | Reliable | Dash-left impulse |
| `Client_DashRightInputRpc` | () | Reliable | Dash-right impulse |
| `Client_ExtendLeftInputRpc` | bool | Reliable | Extend-stick-left held |
| `Client_ExtendRightInputRpc` | bool | Reliable | Extend-stick-right held |
| `Client_LateralLeftInputRpc` | bool | Reliable | Sidestep-left held |
| `Client_LateralRightInputRpc` | bool | Reliable | Sidestep-right held |
| `Client_TalkInputRpc` | bool | Reliable | Voice / talk held |
| `Client_SleepInputRpc` | bool | Reliable | Sleep / rest held |

## Tick loop on the client

```
Update():
    tickAccumulator += Time.deltaTime * 200
    while tickAccumulator >= 1:
        ClientTick()
        tickAccumulator -= 1
```

`ClientTick()` reads each input field, compares to the last value sent,
and fires the matching `Client_*InputRpc` only when it has changed.
Continuous analog inputs (`MoveInput`, `RaycastOriginAngle`,
`LookAngle`) tick-quantize on the *change*, not on every frame, so a
held-down stick does not flood the network with identical values.

## Bot-side per-tick procedure (pseudo)

```
state = compute_desired_inputs(world, target=puck)

if state.move != last.move:           send_move(state.move)
if state.lookAngle != last.lookAngle: send_look(state.lookAngle)
if state.stickAngle != last.stickAngle: send_raycast(state.stickAngle)
if state.bladeAngle != last.bladeAngle: send_blade(state.bladeAngle)

for each bool button:
    if state.btn != last.btn: send_btn(state.btn)

for each impulse (jump/twist/dash):
    if state.btn fired this tick: send_impulse()

last = state
```

## Encoding details for the wire

- `Vector2 → (short, short)`: `(short)(value * 32767)` (already
  game-side; we replicate).
- `degrees → short`: `(short)((angle / 360.0f) * 32767)`. Wraps with
  modulo-style arithmetic so 359° vs −1° look the same.
- `sbyte` blade angle: degrees clamped to [-127, 127].
- All RPCs go through NGO's `[Rpc(SendTo.Server)]` weaver path, so the
  on-wire frame is NGO's standard RPC envelope keyed by `rpcMethodId`
  (uint hash) targeting the bot's Player NetworkObjectId.

## Implications for the bot harness

When the bot reaches the input-streaming phase (after task #16/#17 land
the prefab sync), the input-send code is small: a `BotInputState`
struct, a `LastSent` shadow, and 20 conditional RPC calls per tick.
The harder part is computing `state` from world observation — that's
tasks #6 (skate-to-puck) and #7 (poke + push-to-goal).

We don't need to wait for prefab sync to start writing the input-send
glue and the behavior code. Stubbing the RPC sends as `Debug.Log` lines
lets us iterate on bot behavior offline before plugging into NGO.
