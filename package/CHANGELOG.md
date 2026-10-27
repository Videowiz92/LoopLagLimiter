## 1.1.0
- New: **Gasoline chain guard** (on by default). Gasoline's explosion runs inside the death that triggered it, so a dense
  pack of low-health enemies (e.g. a Halcyon Shrine wave deep into a loop) could chain deep enough to crash the game
  with a stack overflow, freezing it completely. Past 20 levels, the next explosion is queued for the next physics tick;
  every explosion still happens.
- New: **projectile cap**, on by default (500 alive per kind, covers every item). A looped Halcyon Shrine fight had 10,577
  Sticky Bombs alive at once. The old opt-in "Max alive projectiles per prefab" setting is replaced by "Max alive projectiles per kind".
- New: **orb cap**, on by default (300 in flight per kind, covers every item). The same fight had 1,500 Polylute orbs in flight.
- New: the credit cap now also covers the Halcyon Shrine's wave director (seen at 650,000+ credits).
- Fix: projectile counting is now correct even if the game recycles projectile objects, and a burst fired within one tick
  can no longer overshoot the cap.
- New: **Bundle of Fireworks** proc limit (off by default). Fireworks combined with Pocket I.C.B.M. can flood the stage with rockets.
- README: new "How each mechanic works" section explaining every setting, its trade-off and default.
- README: diagnostics keys (F9/F10) only work after turning on "Enable diagnostics" and restarting.

## 1.0.0
- First release.
- On by default: physics catch-up cap, Meat Hook pull cap, removal of orbs with dead targets, camp director credit cap, exception spam guards.
- Optional: per-second proc limits, max alive projectiles per prefab, max visual effects per tick.
- Diagnostics mode with stall reports and deep profiling (off by default).
- Risk of Options settings page with icon (optional dependency).
