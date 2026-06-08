Delta Bar

A live time-delta overlay, like the delta bar in Trackmania. It shows how far
ahead (green) or behind (red) you are, updated continuously, sitting just above
the run timer. Read only, no gameplay changes.

Two modes, picked automatically:
- In the level editor test mode it compares your current run to a recorded trail.
- In online lobbies it compares to your GTR ghost.

Install
1. Put DeltaBar.dll into:  Zeepkist\BepInEx\plugins\
   (a DeltaBar subfolder is fine)
2. Launch the game.

What you need installed
- For the editor version: the "Level Editor Trails" mod. It records the trails
  this compares against, so the delta needs at least one recorded run.
- For the online version: the GTR / ZeepCentraal mod, which provides the ghost.

Editor: last vs fastest
By default it compares to your last run. To compare to your fastest finished run
instead, set this in BepInEx\config\com.aizpun.deltabar.cfg and relaunch:

  [Editor]
  Reference = Fastest

The fastest record clears automatically when you load a different level. You can
also clear it by hand with the F8 key (rebindable as ClearRecordKey in the config).

Notes
- A small debug line shows top left. Turn it off with Debug = false in the config.
- The bar position can be nudged with VerticalNudge in the config.
