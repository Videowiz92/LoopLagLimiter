# LoopLagLimiter

Performance fixes for long, heavily looped Risk of Rain 2 runs, the kind where the game sinks to one frame every few seconds once
the teleporter bosses spawn.

Every fix targets a specific runaway system found by profiling a real stage-45 run. The defaults only kick in at extreme
scale: the projectile and orb caps sit far above anything a normal run produces, and the rest only removes work that can't affect the outcome. Everything is configurable in-game with
[Risk of Options](https://thunderstore.io/package/Rune580/Risk_Of_Options/) (optional) or in the config file.

## What it fixes (on by default)

| Fix | Problem it solves |
|---|---|
| **Physics catch-up cap** (0.1 s) | After a slow frame, Unity runs extra physics ticks to catch up, which makes the next frame slow too. In the test run, every bad frame ran 20 ticks. The cap turns that lag spiral into slow motion, but only when you are below ~10 FPS. |
| **Meat Hook pull cap** (25 per kind) | Sentient Meat Hook spawns a pull effect on every proc. Big builds stacked 300–800 of them, each searching for targets every tick (up to 1.1 s per frame). The oldest are removed first; hook damage is untouched. |
| **Orbs with dead targets** | Polylute, Meat Hook, vine and lightning orbs kept flying after their target died, and could even outlive the stage. They are now removed. |
| **Side director credit cap** (3,000) | When the teleporter finishes, its leftover monster credits go to the stage's camp director (525,196 in the test run), spent on an endless post-teleporter wave. The Halcyon Shrine's wave director was seen with 650,000+. Both are capped. |
| **Projectile cap** (500 alive per kind) | Covers every item. A looped run had 10,577 Sticky Bombs and 4,264 Molten Perforator fireballs alive at once, costing ~14 s of a 24 s frame plus the physics for all of them. |
| **Orb cap** (300 in flight per kind) | Covers every item. 1,500 Polylute orbs were in flight at once in the same run. |
| **Gasoline chain guard** (20 deep) | Gasoline's explosion runs inside the death that triggered it, so a dense pack of low-health enemies (e.g. a Halcyon Shrine wave) chains hundreds of explosions deep and crashes the game with a stack overflow, a total freeze. Deeper explosions are queued for the next physics tick instead; every explosion still happens. |
| **Exception spam guards** | Resonance Disc beams hitting a blocking shield throw an error that cancels the whole beam and writes a full stack trace to the log every time. |

In the run this mod was built from, the worst frame went from **3.7 s to 1.5 s** with only the first fixes in place. The
camp director cap and the catch-up cap address most of what was left.

## Optional limits (off by default)

These trade damage for frame rate. Turn them on only if fights are still too slow.

- **Proc limits**: "at most N per second" for Sentient Meat Hook, Polylute, Ukulele, Plasma Shrimp, Charged Perforator,
  AtG Missile, Sticky Bomb, Molten Perforator, Ceremonial Dagger and Bundle of Fireworks (Pocket I.C.B.M. multiplies the last two), plus your own entries for any orb or projectile.
  Limits apply to what the proc *creates*, so modded copies of an item (e.g. ItemQualities' quality tiers) are covered too.
- **Max visual effects per tick**: visual only.

## Diagnostics (off by default)

Turn on **Enable diagnostics** and restart (F9/F10 do nothing until you do). Whenever a frame takes over 250 ms, a report is written to
`BepInEx/LoopLagLimiter/`. It covers time spent in damage, procs, orbs, projectiles, effects, stat recalcs and directors, who
triggered them, and what exists at that moment. **F10** turns on deep profiling (every component's update in the game and all
mods); **F9** writes a report on demand. Useful for finding what is lagging *your* run.

## Compatibility

- Doesn't reference the game's code directly, so a game update can only disable an individual fix (logged), not crash the mod.
- The limits act on the **host**; clients in multiplayer don't need special setup.
- **Don't use with NoFrameSkip.** The catch-up cap does the same thing. Or set "Max catch-up per frame" to -1 to leave it to NoFrameSkip.
- Works alongside ItemQualities; the proc limits cover its quality items.
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
