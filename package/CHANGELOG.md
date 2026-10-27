## 1.1.0
- New: **Gasoline chain guard** (on by default). Gasoline's explosion runs inside the death that triggered it, so a dense
  pack of low-health enemies (e.g. a Halcyon Shrine wave deep into a loop) could chain deep enough to crash the game
  with a stack overflow, freezing it completely. Past 20 levels, the next explosion is queued for the next physics tick;
  every explosion still happens.
- New: **Bundle of Fireworks** proc limit (off by default). Fireworks combined with Pocket I.C.B.M. can flood the stage with rockets.
- README: diagnostics keys (F9/F10) only work after turning on "Enable diagnostics" and restarting.

## 1.0.0
- First release.
- On by default: physics catch-up cap, Meat Hook pull cap, removal of orbs with dead targets, camp director credit cap, exception spam guards.
- Optional: per-second proc limits, max alive projectiles per prefab, max visual effects per tick.
- Diagnostics mode with stall reports and deep profiling (off by default).
- Risk of Options settings page with icon (optional dependency).
