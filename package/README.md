# LoopLagLimiter

Performance fixes for long, heavily looped Risk of Rain 2 runs, the kind where the game sinks to one frame every few seconds once
the teleporter bosses spawn.

Every fix targets a specific runaway system found by profiling a real stage-45 run. The defaults only kick in at extreme
scale. A normal run never comes near the projectile and orb caps, and the rest only removes work that can't change the outcome.
Everything can be changed in-game with [Risk of Options](https://thunderstore.io/package/Rune580/Risk_Of_Options/) (optional;
hover a setting to read its description) or in `BepInEx/config/com.Videowiz92.LoopLagLimiter.cfg`.

**Results from the run it was built on:** a Halcyon Shrine fight that froze the game, and teleporter fights running at
one frame every 20 seconds, became playable. The worst frame in the same fight went from 24 s to under 3 s.

## At a glance

| On by default | What it stops |
|---|---|
| Physics catch-up cap | The lag spiral where each slow frame makes the next one slower |
| Meat Hook pull cap | Hundreds of stacked pull effects searching for targets every tick |
| Orbs with dead targets | Orbs flying forever at enemies that already died |
| Side director credit cap | Endless enemy waves after the teleporter and Halcyon Shrine |
| Projectile cap | Tens of thousands of Sticky Bombs, fireballs, daggers... alive at once |
| Orb cap | Thousands of Polylute, Meat Hook, Noxious Thorn... orbs in flight at once |
| Projectile fire-rate cap | Items creating and destroying thousands of projectiles per frame (Ghost Blades) |
| Money overflow guard | Gold wrapping to near zero (and showing negative) deep into a loop |
| Gasoline chain guard | A stack-overflow crash that freezes the game completely |
| Exception spam guards | Errors that cancel Resonance Disc beams and flood the log |

| Off by default | |
|---|---|
| Per-item proc limits | "At most N per second" for specific items |
| Visual effect cap | Fewer explosions/sparks drawn per tick |
| Diagnostics | Find out what is lagging *your* run |

## How each mechanic works

### Physics catch-up cap
*Setting: General → Max catch-up per frame (seconds), default 0.1*

Risk of Rain 2 runs its physics and combat at a fixed 60 ticks per second. When a frame takes too long, Unity runs extra ticks
to catch up before drawing the next frame. Those extra ticks make that frame slow too, so it falls further behind: a lag spiral.
In testing, every bad frame was running 20 ticks.

This caps how much catch-up one frame may do. At 0.1 s (about 6 ticks), the game plays in **slow motion instead of freezing**
when it can't keep up, but only below roughly 10 FPS; normal play is unaffected. 0 means one tick per frame (always slow-motion
below 60 FPS); -1 leaves the game's default alone. Same idea as NoFrameSkip, so don't run both.

### Meat Hook pull cap
*Setting: Limits → Max pull effects per prefab, default 25*

Every Sentient Meat Hook proc leaves behind a short-lived pull effect that drags nearby enemies in. At high proc rates these
appear faster than they expire. Test runs had 300–800 at once, each searching for targets every physics tick, costing up to
1.1 s per frame. This keeps the newest 25 of each kind and removes the oldest first. **Hook damage is not affected**, only how
hard enemies get pulled. Applies to any free-standing pull effect, not just Meat Hook's; anything that is or belongs to a
character, ally, minion or deployable is never removed.

### Orbs with dead targets
*Setting: Limits → Drop orbs whose target died, default on*

Many items fire "orbs": Polylute, Meat Hook, Noxious Thorn vines, Ukulele lightning, healing orbs. An orb flies to its target and
then does its effect. If the target dies first, some orbs keep updating anyway, and some even outlived the stage. Since they can
never hit anything, they are removed. Nothing you'd notice is lost.

### Side director credit cap
*Settings: Limits → Max camp director credits (3,000), Camp director names, Also cap Halcyon Shrine wave credits (on)*

Enemies are spawned by "directors" that spend monster credits. When the teleporter finishes, its leftover credits are handed to
the stage's camp director, and the Halcyon Shrine has a wave director of its own. Deep into a loop those leftovers are enormous
(525,000 and 650,000+ credits in testing), so they spawn wave after wave after the fight is over. Each kill then triggers your
on-kill items, which is what made those waves so laggy. These side directors are clamped to 3,000 credits. **The main stage
director and the teleporter boss fight are never touched.**

### Projectile cap
*Setting: Limits → Max alive projectiles per kind, default 500*

Proc items create projectiles: Sticky Bombs, Molten Perforator fireballs, Ceremonial Daggers, missiles, fireworks... At high stacks
nearly every hit creates one, and hits come from everything else, so they pile up faster than they expire. One Halcyon Shrine fight
had 10,577 Sticky Bombs and 4,264 fireballs alive at once, costing about 14 s of a 24 s frame plus physics and network sync for
every one of them.

Once 500 projectiles **of one kind** exist, new ones of that kind aren't fired until some expire. It works for every item,
vanilla or modded, without needing a list. Projectiles that create a character (allies, minions, clones such as Goobo Jr.'s) are never
blocked; a projectile that is itself a deployable, like Ghost Blades, is not an ally and stays limited. The cost is some damage at extreme scale, where everything dies instantly anyway. Host only.

### Orb cap
*Setting: Limits → Max alive orbs per kind, default 300*

The same idea for orbs. Once 300 orbs of one kind are in flight, new ones of that kind wait until some land. Testing saw 1,500
Polylute orbs in flight at once, and Meat Hook and Noxious Thorn orbs hit the cap thousands of times per fight. Host only.

### Projectile fire-rate cap
*Setting: Limits → Max projectiles fired per kind per tick, default 30*

Some projectiles are limited by the game to a few alive at once. Ghost Blades, for example, keep at most 30, and each new one
destroys the oldest. A big enough build fires them so fast that thousands are **created and destroyed every frame**: 4,960 in a
single frame in testing, which froze the game for 33 seconds, almost all of it spent creating and deleting objects. The alive
cap can't catch this, because only 30 ever exist. This caps how many projectiles of one kind may be **fired** per physics tick
(60 per second). It covers every item, and projectiles that spawn allies are never limited. Host only.

### Money overflow guard
*Setting: Limits → Prevent money overflow, default on*

Money is stored as an unsigned 32-bit number. Deep into a loop, per-kill gold pushes it past about 4.29 billion, where it wraps
back to almost nothing; the HUD also shows anything above about 2.15 billion as negative. Gains now stop at 2,147,483,647, the
highest value the HUD shows correctly. Not a lag fix, just a loop-scale bug. Enemy health and damage are floating-point numbers,
so they don't wrap like this. Host only.

### Gasoline chain guard
*Settings: Limits → Max Gasoline chain depth (20), Max queued Gasoline explosions per tick (200)*

Gasoline's explosion happens *inside* the death that triggered it. If the explosion kills another enemy, that death runs Gasoline
again, one level deeper, and so on. In a dense pack of low-health enemies (a Halcyon Shrine wave, say) the chain goes hundreds of
levels deep until the game runs out of stack space and freezes completely (StackOverflowException).

Past 20 levels, the next explosion is put in a queue and fires on the next physics tick, starting a fresh chain. **Every explosion
still happens**, just spread over a few ticks. Host only.

### Exception spam guards
*Settings: Fixes → Suppress known exception spam (on), Methods to guard*

Two game methods throw errors over and over in late runs. The worst is Resonance Disc beams hitting a blocking shield: the error
cancels the entire beam, and its full stack trace is written to the log (console, BepInEx GUI and disk) every time. The error
is caught, the beam treats the shield as "not blocking" and carries on, and only the first occurrence is logged.

### Per-item proc limits (off by default)
*Settings: Proc limits → one "per second" slider per item, plus Extra limits*

This is the finer-grained option. Where the caps above limit how many of something **exist at once**, these limit how often a
specific item may **fire**: e.g. "at most 30 Sticky Bombs per second". Covered: Sentient Meat Hook, Polylute, Ukulele, Plasma
Shrimp, Charged Perforator, AtG Missile, Sticky Bomb, Molten Perforator, Ceremonial Dagger and Bundle of Fireworks (Pocket I.C.B.M.
multiplies missiles and fireworks). The limit applies to what the proc creates, so modded copies of an item (like ItemQualities'
quality tiers) count toward the same limit. **Extra limits** takes any other orb or projectile as `Name=N;Name=N`; diagnostics
reports show the names. Fewer procs means less damage.

### Visual effect cap (off by default)
*Setting: Limits → Max visual effects per tick*

Limits how many explosions, sparks and impact effects are spawned per physics tick. Purely cosmetic; try 150 if effects are what's
slowing you down.

### Diagnostics (off by default)
*Settings: Diagnostics → Enable diagnostics (restart required), keys F9 / F10*

When on, every frame slower than 250 ms writes a report to `BepInEx/LoopLagLimiter/` (and the log). It shows time spent in
damage, procs, orbs, projectiles, effects, stat recalculation and directors, who triggered each, what exists at that moment
(bodies, projectiles, orbs, physics objects, director credits), and how often each limit fired. **F10** turns on deep profiling,
which times every component's update in the game and every mod (a few seconds' hitch when toggled). **F9** writes a report on
demand. F9/F10 do nothing until diagnostics are enabled.

## Compatibility

- Doesn't reference the game's code directly, so a game update can only disable an individual fix (logged), not crash the mod.
- Most limits act on the **host**. Clients in multiplayer don't need special setup, and players without the mod can still join.
- **Don't use with NoFrameSkip.** The catch-up cap does the same thing. Or set "Max catch-up per frame" to -1 to leave it to NoFrameSkip.
- Works alongside ItemQualities; the caps and proc limits cover its quality items.
- **Allies are never touched.** The only things ever removed are pull effects and orbs whose target died. Allies that
  disappear soon after spawning are the game's own limits (each ally type has a maximum count; the oldest is replaced).
- Pausing doesn't remove lag that comes from how much is on screen. The game still has to draw everything while paused.
- Updating keeps your existing settings. A new default only applies to settings you haven't saved yet.
- If the game ever sticks on a white screen at boot with this mod, set `Register Risk of Options page = false` in section
  "0. Troubleshooting" of the config and report it on GitHub.

## Credits

- **ProcLimiter** by DrinkableWater ([source](https://github.com/Sylkadi/ProcLimiter)): the idea of per-item proc cooldowns
  to fight late-game lag. ProcLimiter is deprecated and was built for game version 1.2.3. LoopLagLimiter's proc limits are a
  from-scratch reimplementation (no code reused) that works on current versions and covers modded item variants.
- **NoFrameSkip** by McAwesome ([source](https://github.com/McAwesome123/NoFrameSkip), MIT): the idea of capping
  `Time.maximumDeltaTime` to stop the physics catch-up spiral. Reimplemented here; please support the original.
- **Risk of Options** by Rune580: in-game settings page.

Made by Videowiz92.
