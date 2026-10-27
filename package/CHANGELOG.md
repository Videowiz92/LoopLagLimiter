## 1.2.0
- New: **projectile fire-rate cap**, on by default (30 per kind per physics tick, covers every item). Ghost Blades are kept at
  30 alive by the game, so each new blade destroys the oldest; 4,960 were created and destroyed in one frame, freezing the game
  for 33 seconds. The alive cap couldn't catch that because only 30 ever existed.
- Fix: the "never block ally-spawning projectiles" check also matched projectiles that are themselves deployables (Ghost Blades),
  which exempted them from every cap. It now only matches projectiles that create a character (ally, minion, clone), and the
  log says which component matched.
- New: **money overflow guard**, on by default. Money stops at 2,147,483,647 instead of wrapping past ~4.29 billion back to
  near zero (and showing as negative on the HUD above ~2.15 billion).
- New: **price overflow guard**, on by default. Prices that overflow into negatives (base cost times a huge loop difficulty
  multiplier) become 2,147,483,647, the same ceiling as money, so items stay buyable once money is maxed.

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
- Safety: the pull cap only removes free-standing pull effects (never characters, allies, minions or deployables), and
  projectiles that spawn something (allies, minions, clones like Goobo Jr.'s) are never blocked by any cap or limit.
- New: **Bundle of Fireworks** proc limit (off by default). Fireworks combined with Pocket I.C.B.M. can flood the stage with rockets.
- README: new "How each mechanic works" section explaining every setting, its trade-off and default.
- README: diagnostics keys (F9/F10) only work after turning on "Enable diagnostics" and restarting.

## 1.0.0
- First release.
- On by default: physics catch-up cap, Meat Hook pull cap, removal of orbs with dead targets, camp director credit cap, exception spam guards.
- Optional: per-second proc limits, max alive projectiles per prefab, max visual effects per tick.
- Diagnostics mode with stall reports and deep profiling (off by default).
- Risk of Options settings page with icon (optional dependency).
