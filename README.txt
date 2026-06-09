Delta Bar

A live time-delta overlay, like the delta bar in Trackmania. It shows how far
ahead (green) or behind (red) you are, updated continuously, sitting just above
the run timer. Read only, no gameplay changes.

Two modes, picked automatically:
- In the level editor test mode it compares your current run to a recorded trail.
- In free play and time trial it compares to your GTR ghost.

The ghost delta is free play only. It is always disabled in online lobbies, the
same way GTR only shows ghosts outside lobbies, so it gives no advantage in a race
against other people.

Each mode has its own on/off switch in Zeep Settings (or the config file), so you
can run just the editor delta, just the free play delta, or both. Both are on by
default.

Install
1. Put DeltaBar.dll into:  Zeepkist\BepInEx\plugins\
   (a DeltaBar subfolder is fine)
2. Launch the game.

What you need installed
- For the editor version: the "Level Editor Trails" mod. It records the trails
  this compares against, so the delta needs at least one recorded run.
- For the free play version: the GTR / ZeepCentraal mod, which provides the ghost.

Editor: last vs fastest
By default it compares to your last run. To compare to your fastest finished run
instead, set this in BepInEx\config\com.aizpun.deltabar.cfg and relaunch:

  [Editor]
  Reference = Fastest

The fastest record clears automatically when you load a different level. You can
also clear it by hand with the F8 key (rebindable as ClearRecordKey in the config).

Moving the bar
The bar is registered with the ZeepSDK UI configurator, so you move and resize it
the same way as the rest of the HUD: open the configurator (its key is in ZeepSDK's
settings), cycle to the "DeltaBar" element, and drag or scale it. The position is
saved. By default it sits bottom centre, above the run timer. To go back to the old
fixed position above the timer, set Movable = false under [Bar] in the config.

Notes
- A small debug line shows top left. Turn it off with Debug = false in the config.
- VerticalNudge only applies when Movable is off (fixed timer-anchored position).
