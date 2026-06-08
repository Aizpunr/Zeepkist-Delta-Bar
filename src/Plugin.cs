using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

// Delta Bar - Zeepkist BepInEx plugin.
//
// PHASE 1: live Trackmania-style time delta vs a GTR ghost.
//   * Ghost data: reached by reflection into GTR's in-memory decoded ghost
//       Chainloader -> GTR plugin -> DI host -> GetService(GhostPlayer)
//       -> ActiveGhosts -> GhostData.Ghost -> private _frames (IFrame: Time/Position).
//     Captured once into a (time,pos) polyline when the active ghost changes.
//   * Live car: PlayerManager.Instance.currentMaster.carSetups[0] (SetupCar),
//     read as a UnityEngine.Component -> transform.position. SAME world space GTR
//     records ghosts in, so projection lines up exactly.
//   * Each frame: project car position onto the ghost polyline (forward-windowed
//     cursor, global re-acquire when off-line), interpolate the ghost time at that
//     point, delta = myRaceTime (GhostTimingService.CurrentTime) - ghostTimeThere.
//     Negative = ahead (green), positive = behind (red).
//
// Read-only overlay: no gameplay interference, no server comms. Inside the rules.
// C# 5 ONLY (Windows built-in csc): no string interpolation -> string.Format.

namespace DeltaBar
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(GtrGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class DeltaBarPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.aizpun.deltabar";
        public const string PluginName = "Delta Bar";
        public const string PluginVersion = "0.3.0";

        private const string GtrGuid = "net.tnrd.zeepkist.gtr";
        private const string GhostPlayerTypeName = "TNRD.Zeepkist.GTR.Ghosting.Playback.GhostPlayer";
        private const string GhostTimingTypeName = "TNRD.Zeepkist.GTR.Ghosting.Playback.GhostTimingService";
        private const string IFrameTypeName = "TNRD.Zeepkist.GTR.Ghosting.Ghosts.IFrame";

        private const BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Projection tuning (metres).
        private const float ReacquireDist = 8f;   // within this, trust the windowed cursor
        private const float ValidDist = 30f;       // beyond this, treat as off-track -> hide
        private const int WinBack = 10;            // segments to search behind the cursor
        private const int WinFwd = 140;            // segments to search ahead of the cursor

        // Config
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _enableOnline;   // show in online lobbies (vs GTR ghost)
        private ConfigEntry<bool> _enableEditor;   // show in the level editor (vs a trail)
        private ConfigEntry<float> _maxDelta;      // bar saturates at this many seconds
        private ConfigEntry<bool> _debug;
        private ConfigEntry<float> _yNudge;        // fine-tune vertical position (pixels, timer-anchor fallback only)
        private ConfigEntry<bool> _useConfigurator; // make the bar movable via ZeepSDK's UI configurator
        private ConfigEntry<EditorReference> _editorRef;
        private ConfigEntry<KeyCode> _clearRecordKey;
        public enum EditorReference { Last, Fastest }

        // GTR reflection handles
        private object _ghostPlayer;
        private object _timing;
        private Assembly _gtrAssembly;
        private PropertyInfo _activeGhostsProp;
        private PropertyInfo _ghostDataGhostProp;
        private PropertyInfo _ghostDataTypeProp;
        private PropertyInfo _frameTime;
        private PropertyInfo _framePos;
        private PropertyInfo _currentTimeProp;
        private float _resolveTimer;

        // PlayerManager reflection handles
        private Type _pmType;
        private PropertyInfo _pmInstanceGetter;

        // Captured ghost polyline
        private object _capturedGhost;
        private bool _haveGhost;
        private float[] _t;
        private Vector3[] _p;
        private string _ghostType = "?";

        // Per-frame projection state
        private int _cursor;
        private bool _needReacquire = true;
        private float _lastTime;
        private float _delta;
        private bool _show;
        private float _lastDist, _lastGhostT, _lastCt;

        // Run-timer anchor: found by matching the live MM:SS.mmm text and taking the
        // bottom-most match (the bottom-centre run timer, not the lobby/PB displays).
        private Type _tmpType;
        private PropertyInfo _tmpTextProp;
        private Component _timerComp;
        private float _anchorRefindTimer;
        private readonly Vector3[] _corners = new Vector3[4];

        // Movable bar: our own overlay canvas + a named RectTransform registered with
        // ZeepSDK's UI configurator (UIApi.AddToConfigurator), so the user drags/scales it
        // with the same tool as the rest of the HUD and the position persists (keyed by the
        // transform's hierarchy path). Reflected, so ZeepSDK stays a soft dependency.
        private RectTransform _uiRoot;
        private bool _uiCreated, _uiRegistered;
        private float _uiRetry;
        private MethodInfo _uiApiAddMethod;

        // Editor mode: delta vs a LevelEditorTrails recording (no GTR ghost needed).
        private bool _editorResolved, _editorAvailable;
        private PropertyInfo _loadedTrailsProp;          // static TrailManager.LoadedTrails
        private Type _recorderType;                       // TrailRecorder (MonoBehaviour)
        private FieldInfo _recorderTimeField;             // TrailRecorder._time
        private FieldInfo _trailFramesField;             // Trail.Frames
        private FieldInfo _frameTimeField, _framePosField; // TrailFrame.Time / .Position
        private UnityEngine.Object _activeRecorder;
        private float _recRefindTimer;
        private object _capturedTrail;
        private FieldInfo _recorderTrailField;            // TrailRecorder._trail (live run)
        private float[] _bestT;                            // locked fastest-finished snapshot
        private Vector3[] _bestP;
        private float _bestDuration = float.MaxValue;
        private bool _levelLoadHooked;
        public static DeltaBarPlugin Instance;
        private Harmony _harmony;

        private string _status = "starting";

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true, "Master switch for the delta bar.");
            _enableOnline = Config.Bind("Online", "Enabled", true, "Show the delta bar in online lobbies (vs your GTR ghost).");
            _enableEditor = Config.Bind("Editor", "Enabled", true, "Show the delta bar in the level editor (vs a recorded trail).");
            _maxDelta = Config.Bind("General", "MaxDeltaSeconds", 2f, "Delta (seconds) at which the bar is fully filled.");
            _debug = Config.Bind("General", "Debug", true, "Show a small debug readout (delta, projection distance, times).");
            _yNudge = Config.Bind("General", "VerticalNudge", 0f, "Fine-tune the bar's vertical position in pixels (+ down, - up). Only used when the configurator is off.");
            _useConfigurator = Config.Bind("Bar", "Movable", true, "Make the bar movable with the ZeepSDK UI configurator (drag/scale it like the rest of the HUD; position is saved). If off, the bar anchors above the run timer.");
            _editorRef = Config.Bind("Editor", "Reference", EditorReference.Last,
                "In the level editor: compare vs your Last run, or your Fastest finished run.");
            _clearRecordKey = Config.Bind("Editor", "ClearRecordKey", KeyCode.F8,
                "Key to clear the saved Fastest editor record. It also auto-clears when you load a different level.");
            Instance = this;
            SetupFinishHook();
            Logger.LogInfo(string.Format("{0} {1} loaded (Phase 1).", PluginName, PluginVersion));
        }

        // Finish detection for editor "vs Fastest": a Harmony postfix on the game's
        // ReadyToReset.HeyYouHitATrigger (the same hook EditorSpeedSplits uses). The
        // method's isFinish arg separates finish from checkpoint. Only acts in the editor
        // (OnEditorFinish needs an active TrailRecorder); harmless online.
        private void SetupFinishHook()
        {
            try
            {
                Type rtr = AccessTools.TypeByName("ReadyToReset");
                if (rtr == null) { Logger.LogWarning("DeltaBar: ReadyToReset not found; vs Fastest disabled."); return; }
                MethodInfo target = AccessTools.Method(rtr, "HeyYouHitATrigger");
                if (target == null) { Logger.LogWarning("DeltaBar: HeyYouHitATrigger not found; vs Fastest disabled."); return; }
                _harmony = new Harmony(PluginGuid);
                MethodInfo post = typeof(DeltaBarFinishPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(target, null, new HarmonyMethod(post));
                Logger.LogInfo("DeltaBar: finish hook installed (ReadyToReset.HeyYouHitATrigger).");
            }
            catch (Exception e) { Logger.LogWarning("DeltaBar: finish hook failed: " + e.Message); }
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
        }

        private void Update()
        {
            try
            {
                if (_clearRecordKey != null && _clearRecordKey.Value != KeyCode.None
                    && Input.GetKeyDown(_clearRecordKey.Value)) ClearFastestRecord("keybind");

                EnsureConfiguratorRect();   // create + register the movable bar (no-op once done)

                // Editor test run active (a LevelEditorTrails recorder exists) -> editor
                // mode (reference = a recorded trail). Otherwise online mode (GTR ghost).
                float ct;
                if (TryEditorClock(out ct))
                {
                    if (!_enableEditor.Value) { _show = false; return; }
                    if (!CaptureEditorRef()) { _show = false; return; }
                }
                else
                {
                    if (!_enableOnline.Value) { _status = "online: disabled in settings"; _show = false; return; }
                    if (!EnsureGtr()) { _show = false; return; }
                    CaptureGhostIfChanged();
                    if (!_haveGhost) { _status = "online: waiting for ghost"; _show = false; return; }
                    ct = ReadCurrentTime();
                }

                if (ct + 0.05f < _lastTime) _needReacquire = true; // run reset / new attempt
                _lastTime = ct;
                if (ct <= 0f) { _show = false; return; }            // not running yet

                Vector3 me;
                if (!TryGetLocalCarPos(out me)) { _show = false; return; }

                float tGhost, dist;
                if (!FindProjection(me, out tGhost, out dist)) { _show = false; return; }

                _delta = ct - tGhost;
                _lastCt = ct; _lastGhostT = tGhost; _lastDist = dist;
                _show = true;
            }
            catch (Exception e)
            {
                _status = "error: " + e.Message;
                _show = false;
            }
        }

        // ---- GTR resolution -------------------------------------------------

        private bool EnsureGtr()
        {
            if (_ghostPlayer != null) return true;
            _resolveTimer += Time.deltaTime;
            if (_resolveTimer < 0.5f) return false;
            _resolveTimer = 0f;

            PluginInfo gtr;
            if (!Chainloader.PluginInfos.TryGetValue(GtrGuid, out gtr) || gtr.Instance == null)
            { _status = "GTR plugin not found yet"; return false; }

            object plugin = gtr.Instance;
            _gtrAssembly = plugin.GetType().Assembly;

            IServiceProvider provider = FindServiceProvider(plugin);
            if (provider == null) { _status = "GTR provider not found yet"; return false; }

            Type gpType = _gtrAssembly.GetType(GhostPlayerTypeName);
            if (gpType == null) { _status = "GhostPlayer type missing"; return false; }
            object gp = provider.GetService(gpType);
            if (gp == null) { _status = "GhostPlayer not resolved yet"; return false; }

            _ghostPlayer = gp;
            _activeGhostsProp = gpType.GetProperty("ActiveGhosts", AllInstance);

            Type timingType = _gtrAssembly.GetType(GhostTimingTypeName);
            if (timingType != null)
            {
                _timing = provider.GetService(timingType);
                if (_timing != null) _currentTimeProp = timingType.GetProperty("CurrentTime", AllInstance);
            }

            Type iframe = _gtrAssembly.GetType(IFrameTypeName);
            if (iframe != null)
            {
                _frameTime = iframe.GetProperty("Time");
                _framePos = iframe.GetProperty("Position");
            }

            Logger.LogInfo("Delta Bar: resolved GTR GhostPlayer + timing via DI host.");
            _status = "resolved";
            return true;
        }

        private IServiceProvider FindServiceProvider(object plugin)
        {
            IServiceProvider fallback = null;
            Type t = plugin.GetType();
            while (t != null && t != typeof(object))
            {
                FieldInfo[] fields = t.GetFields(AllInstance);
                for (int i = 0; i < fields.Length; i++)
                {
                    object v;
                    try { v = fields[i].GetValue(plugin); } catch { continue; }
                    if (v == null) continue;
                    Type vt = v.GetType();

                    bool isHost = false;
                    Type[] ifaces = vt.GetInterfaces();
                    for (int k = 0; k < ifaces.Length; k++)
                        if (ifaces[k].FullName == "Microsoft.Extensions.Hosting.IHost") { isHost = true; break; }

                    PropertyInfo svc = vt.GetProperty("Services");
                    if (svc != null)
                    {
                        IServiceProvider sp = svc.GetValue(v, null) as IServiceProvider;
                        if (sp != null) { if (isHost) return sp; if (fallback == null) fallback = sp; }
                    }
                    IServiceProvider direct = v as IServiceProvider;
                    if (direct != null && fallback == null) fallback = direct;
                }
                t = t.BaseType;
            }
            return fallback;
        }

        private float ReadCurrentTime()
        {
            if (_timing == null || _currentTimeProp == null) return 0f;
            try { return (float)_currentTimeProp.GetValue(_timing, null); } catch { return 0f; }
        }

        // ---- Editor reference (LevelEditorTrails) ---------------------------

        // Resolve LevelEditorTrails' static types/fields. Absent if the mod isn't
        // installed -> editor mode just never activates, online still works.
        private bool EnsureEditorReflection()
        {
            if (_editorResolved) return _editorAvailable;
            _editorResolved = true;

            Assembly let = null;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type t = null; try { t = asms[i].GetType("TNRD.Zeepkist.LevelEditorTrails.TrailManager"); } catch { }
                if (t != null) { let = asms[i]; break; }
            }
            if (let == null) return false;

            Type tm = let.GetType("TNRD.Zeepkist.LevelEditorTrails.TrailManager");
            _recorderType = let.GetType("TNRD.Zeepkist.LevelEditorTrails.TrailRecorder");
            Type trail = let.GetType("TNRD.Zeepkist.LevelEditorTrails.Trail");
            Type frame = let.GetType("TNRD.Zeepkist.LevelEditorTrails.TrailFrame");
            if (tm == null || _recorderType == null || trail == null || frame == null) return false;

            _loadedTrailsProp = tm.GetProperty("LoadedTrails",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            _recorderTimeField = _recorderType.GetField("_time", AllInstance);
            _recorderTrailField = _recorderType.GetField("_trail", AllInstance);
            _trailFramesField = trail.GetField("Frames", AllInstance);
            _frameTimeField = frame.GetField("Time", AllInstance);
            _framePosField = frame.GetField("Position", AllInstance);

            _editorAvailable = _loadedTrailsProp != null && _recorderTimeField != null
                && _trailFramesField != null && _frameTimeField != null && _framePosField != null;
            if (_editorAvailable)
            {
                Logger.LogInfo("Delta Bar: LevelEditorTrails detected, editor mode armed.");
                HookLevelLoad(tm);
            }
            return _editorAvailable;
        }

        // Auto-clear the locked fastest run when the editor loads a different level: the
        // record belongs to the old level/layout. TrailManager.LoadTrails runs on every
        // level load (even with zero saved trails), so it is the exact level-change signal.
        // Patched lazily here (LevelEditorTrails is confirmed loaded) to dodge load order.
        private void HookLevelLoad(Type trailManager)
        {
            if (_levelLoadHooked) return;
            _levelLoadHooked = true;
            try
            {
                MethodInfo lt = AccessTools.Method(trailManager, "LoadTrails");
                if (lt == null) { Logger.LogWarning("DeltaBar: LoadTrails not found; per-level reset disabled."); return; }
                if (_harmony == null) _harmony = new Harmony(PluginGuid);
                MethodInfo post = typeof(DeltaBarLevelLoadPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(lt, null, new HarmonyMethod(post));
                Logger.LogInfo("DeltaBar: level-change hook installed (TrailManager.LoadTrails).");
            }
            catch (Exception e) { Logger.LogWarning("DeltaBar: level-change hook failed: " + e.Message); }
        }

        // Called from the level-load hook.
        public void OnEditorLevelChanged() { ClearFastestRecord("level change"); }

        private void ClearFastestRecord(string reason)
        {
            _bestT = null; _bestP = null; _bestDuration = float.MaxValue;
            if (_ghostType == "editor:fastest") _capturedTrail = null;  // force the Fastest branch to re-evaluate
            Logger.LogInfo("Delta Bar editor: fastest record cleared (" + reason + ").");
        }

        // True only while an editor test run is active (a TrailRecorder exists). ct is
        // that recorder's _time, the exact clock the trail frames are stamped with.
        private bool TryEditorClock(out float ct)
        {
            ct = 0f;
            if (!EnsureEditorReflection()) return false;

            if (_activeRecorder == null)                  // null or Unity-destroyed (between runs)
            {
                _recRefindTimer += Time.deltaTime;
                if (_recRefindTimer < 0.3f) return false;
                _recRefindTimer = 0f;
                _activeRecorder = UnityEngine.Object.FindObjectOfType(_recorderType);
                if (_activeRecorder == null) return false;
            }
            ct = (float)_recorderTimeField.GetValue(_activeRecorder);
            return true;
        }

        // Reference = most recent completed trail (vs Last). LevelEditorTrails commits a
        // trail on run end, so LoadedTrails[last] is your previous attempt, not the
        // in-progress one. Captures it into the shared _t/_p polyline when it changes.
        private bool CaptureEditorRef()
        {
            if (_editorRef.Value == EditorReference.Fastest)
            {
                if (_bestP == null || _bestP.Length < 2) { _status = "editor: no finished run yet (vs Fastest)"; return false; }
                if (!ReferenceEquals(_bestP, _capturedTrail))
                {
                    _t = _bestT; _p = _bestP;          // share the immutable locked snapshot
                    _capturedTrail = _bestP;
                    _ghostType = "editor:fastest";
                    _cursor = 0; _needReacquire = true;
                }
                return true;
            }

            IList trails = _loadedTrailsProp.GetValue(null, null) as IList;
            if (trails == null || trails.Count == 0)
            { _capturedTrail = null; _status = "editor: drive a run to set a reference"; return false; }

            object refTrail = trails[trails.Count - 1];
            if (refTrail == null) return false;
            if (ReferenceEquals(refTrail, _capturedTrail)) return _p != null && _p.Length >= 2;

            IList frames = _trailFramesField.GetValue(refTrail) as IList;
            if (frames == null || frames.Count < 2) return false;

            int c = frames.Count;
            _t = new float[c];
            _p = new Vector3[c];
            for (int i = 0; i < c; i++)
            {
                object f = frames[i];
                _t[i] = (float)_frameTimeField.GetValue(f);
                _p[i] = (Vector3)_framePosField.GetValue(f);
            }
            _capturedTrail = refTrail;
            _ghostType = "editor:last";
            _cursor = 0; _needReacquire = true;
            Logger.LogInfo(string.Format("Delta Bar editor: captured last trail, frames={0} dur={1:0.000}s", c, _t[c - 1]));
            return true;
        }

        // Called from the finish Harmony hook (isFinish==true). If we're in an editor
        // test run and this run is faster than the locked best, snapshot it (own copy,
        // immune to LevelEditorTrails' FIFO eviction). Duration = recorder _time at finish.
        public void OnEditorFinish()
        {
            try
            {
                if (!EnsureEditorReflection() || _recorderTrailField == null) return;
                UnityEngine.Object rec = _activeRecorder;
                if (rec == null) rec = UnityEngine.Object.FindObjectOfType(_recorderType);
                if (rec == null) return;                       // not an editor test run -> ignore

                float dur = (float)_recorderTimeField.GetValue(rec);
                if (dur <= 0f || dur >= _bestDuration) return; // not a faster finished run

                object trail = _recorderTrailField.GetValue(rec);
                if (trail == null) return;
                IList frames = _trailFramesField.GetValue(trail) as IList;
                if (frames == null || frames.Count < 2) return;

                int c = frames.Count;
                float[] bt = new float[c];
                Vector3[] bp = new Vector3[c];
                for (int i = 0; i < c; i++)
                {
                    object f = frames[i];
                    bt[i] = (float)_frameTimeField.GetValue(f);
                    bp[i] = (Vector3)_framePosField.GetValue(f);
                }
                _bestT = bt; _bestP = bp; _bestDuration = dur;
                Logger.LogInfo(string.Format("Delta Bar editor: new fastest finished run {0:0.000}s ({1} frames)", dur, c));
            }
            catch { }
        }

        // ---- Ghost capture --------------------------------------------------

        private void CaptureGhostIfChanged()
        {
            IEnumerable gds = _activeGhostsProp != null
                ? _activeGhostsProp.GetValue(_ghostPlayer, null) as IEnumerable : null;

            object chosenGhost = null; string type = "?";
            if (gds != null)
            {
                foreach (object gd in gds)
                {
                    if (gd == null) continue;
                    if (_ghostDataGhostProp == null) _ghostDataGhostProp = gd.GetType().GetProperty("Ghost", AllInstance);
                    object g = _ghostDataGhostProp != null ? _ghostDataGhostProp.GetValue(gd, null) : null;
                    if (g == null) continue;
                    chosenGhost = g;
                    if (_ghostDataTypeProp == null) _ghostDataTypeProp = gd.GetType().GetProperty("Type", AllInstance);
                    if (_ghostDataTypeProp != null) type = "" + _ghostDataTypeProp.GetValue(gd, null);
                    break; // first loaded ghost (selection config: later)
                }
            }

            if (chosenGhost == null) { _haveGhost = false; _capturedGhost = null; return; }
            if (ReferenceEquals(chosenGhost, _capturedGhost)) return;

            if (CaptureFrames(chosenGhost))
            {
                _capturedGhost = chosenGhost;
                _haveGhost = true;
                _ghostType = type;
                _cursor = 0;
                _needReacquire = true;
                Logger.LogInfo(string.Format("Delta Bar: captured ghost type={0} frames={1}", type, _p.Length));
            }
            else { _haveGhost = false; }
        }

        private bool CaptureFrames(object ghost)
        {
            FieldInfo ff = ghost.GetType().GetField("_frames", AllInstance);
            if (ff == null) return false;
            IEnumerable fe = ff.GetValue(ghost) as IEnumerable;
            if (fe == null) return false;

            List<object> raw = new List<object>();
            foreach (object f in fe) raw.Add(f);
            int c = raw.Count;
            if (c < 2) return false;

            PropertyInfo pt = _frameTime != null ? _frameTime : raw[0].GetType().GetProperty("Time");
            PropertyInfo pp = _framePos != null ? _framePos : raw[0].GetType().GetProperty("Position");
            if (pt == null || pp == null) return false;

            _t = new float[c];
            _p = new Vector3[c];
            for (int i = 0; i < c; i++)
            {
                _t[i] = (float)pt.GetValue(raw[i], null);
                _p[i] = (Vector3)pp.GetValue(raw[i], null);
            }
            return true;
        }

        // ---- Live car position ---------------------------------------------

        private bool EnsurePlayerManager()
        {
            if (_pmType != null) return _pmInstanceGetter != null;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type t = null;
                try { t = asms[i].GetType("PlayerManager"); } catch { }
                if (t != null) { _pmType = t; break; }
            }
            if (_pmType == null) return false;
            _pmInstanceGetter = _pmType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return _pmInstanceGetter != null;
        }

        private bool TryGetLocalCarPos(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!EnsurePlayerManager()) return false;
            object pm = _pmInstanceGetter.GetValue(null, null);
            if (pm == null) return false;
            object master = GetMember(pm, "currentMaster");
            if (master == null) return false;
            object setups = GetMember(master, "carSetups");
            IList list = setups as IList;
            if (list == null || list.Count == 0) return false;
            Component car = list[0] as Component;
            if (car == null) return false;
            pos = car.transform.position;
            return true;
        }

        private object GetMember(object obj, string name)
        {
            Type t = obj.GetType();
            PropertyInfo p = t.GetProperty(name, AllInstance);
            if (p != null) { try { return p.GetValue(obj, null); } catch { } }
            FieldInfo f = t.GetField(name, AllInstance);
            if (f != null) { try { return f.GetValue(obj); } catch { } }
            return null;
        }

        // ---- Projection -----------------------------------------------------

        private bool FindProjection(Vector3 me, out float tGhost, out float bestDist)
        {
            tGhost = 0f; bestDist = float.MaxValue;
            if (_p == null || _p.Length < 2) return false;
            int n = _p.Length;

            int lo, hi;
            if (_needReacquire) { lo = 0; hi = n - 2; }
            else { lo = Mathf.Max(0, _cursor - WinBack); hi = Mathf.Min(n - 2, _cursor + WinFwd); }

            int bi = lo; float bt = 0f, best = float.MaxValue;
            for (int i = lo; i <= hi; i++)
            {
                float d, t = ClosestParam(me, _p[i], _p[i + 1], out d);
                if (d < best) { best = d; bi = i; bt = t; }
            }

            _cursor = bi;
            bestDist = best;
            tGhost = Mathf.Lerp(_t[bi], _t[bi + 1], bt);

            _needReacquire = best > ReacquireDist;   // re-anchor globally next frame if drifting
            return best <= ValidDist;
        }

        private static float ClosestParam(Vector3 me, Vector3 a, Vector3 b, out float dist)
        {
            Vector3 ab = b - a;
            float l2 = Vector3.Dot(ab, ab);
            float t = l2 > 1e-9f ? Vector3.Dot(me - a, ab) / l2 : 0f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            Vector3 c = new Vector3(a.x + ab.x * t, a.y + ab.y * t, a.z + ab.z * t);
            dist = Vector3.Distance(me, c);
            return t;
        }

        // ---- Rendering ------------------------------------------------------

        private GUIStyle _numStyle, _smallStyle;

        private void OnGUI()
        {
            if (_enabled == null || !_enabled.Value) return;
            EnsureStyles();

            if (_debug.Value)
            {
                Color o = GUI.color; GUI.color = Color.yellow;
                GUI.Label(new Rect(10f, 10f, 1100f, 20f), "DeltaBar: " + DebugLine(), _smallStyle);
                GUI.color = o;
            }

            if (!_show) return;

            Rect box;
            if (!TryGetBarBox(out box)) return;
            DrawBar(box);
        }

        // The bar's screen box (GUI top-left coords). Prefer the configurator-registered
        // RectTransform (movable + scalable + persisted); fall back to a fixed-size box
        // above the run timer, then to a bottom-centre default if even that is missing.
        private bool TryGetBarBox(out Rect box)
        {
            box = default(Rect);

            if (_uiRoot != null)
            {
                _uiRoot.GetWorldCorners(_corners);        // screen px, origin bottom-left
                float l = _corners[0].x, r = _corners[2].x;
                float bottomY = _corners[0].y, topY = _corners[1].y;
                float w = r - l, h = topY - bottomY;
                if (w > 1f && h > 1f) { box = new Rect(l, Screen.height - topY, w, h); return true; }
            }

            // Fallback: build a fixed box where the old timer-anchored bar used to sit.
            const float bw = 560f, barH = 28.6f, numH = 22f, gap = 6f;
            float cx, lapTopY, barTop;
            if (TryGetLapTimeAnchor(out cx, out lapTopY)) barTop = lapTopY - gap - barH;
            else { cx = Screen.width * 0.5f; barTop = Screen.height * 0.12f; }
            barTop += _yNudge.Value;
            box = new Rect(cx - bw * 0.5f, barTop - numH, bw, barH + numH);
            return true;
        }

        // Draw the delta number (top of the box) and the bar (bottom of the box), filling
        // the given screen box so it tracks the configurator's move/scale.
        private void DrawBar(Rect box)
        {
            float maxD = _maxDelta.Value; if (maxD < 0.1f) maxD = 0.1f;

            float barH = box.height * 0.56f;
            float barTop = box.yMax - barH;
            float cx = box.x + box.width * 0.5f;
            float half = box.width * 0.5f;

            DrawRect(new Rect(box.x, barTop, box.width, barH), new Color(0f, 0f, 0f, 0.55f));

            bool ahead = _delta <= 0f;
            float fw = Mathf.Clamp01(Mathf.Abs(_delta) / maxD) * half;
            Color fill = ahead ? new Color(0.15f, 0.85f, 0.25f, 0.9f) : new Color(0.9f, 0.2f, 0.2f, 0.9f);
            if (ahead) DrawRect(new Rect(cx - fw, barTop, fw, barH), fill);
            else DrawRect(new Rect(cx, barTop, fw, barH), fill);

            DrawRect(new Rect(cx - 1f, barTop - 2f, 2f, barH + 4f), Color.white);

            int fs = (int)(box.height * 0.42f); if (fs < 8) fs = 8; if (fs > 48) fs = 48;
            _numStyle.fontSize = fs;
            string txt = string.Format("{0:+0.00;-0.00;0.00}", _delta);
            Color oc = GUI.color;
            GUI.color = ahead ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.6f, 0.6f);
            GUI.Label(new Rect(box.x, box.y, box.width, box.height - barH), txt, _numStyle);
            GUI.color = oc;
        }

        // Create our overlay canvas + a stable-named RectTransform once, then register it
        // with ZeepSDK's UI configurator (retried until ZeepSDK is up). Default position is
        // a bottom-centre box, so the bar is visible even before the user moves it and
        // regardless of where the rest of the HUD was relocated.
        private void EnsureConfiguratorRect()
        {
            if (_useConfigurator == null || !_useConfigurator.Value) return;

            if (!_uiCreated)
            {
                GameObject canvasGo = new GameObject("DeltaBarCanvas");
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);
                Canvas canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000;

                GameObject barGo = new GameObject("DeltaBar", new Type[] { typeof(RectTransform) });
                barGo.transform.SetParent(canvasGo.transform, false);
                RectTransform rt = (RectTransform)barGo.transform;
                rt.anchorMin = new Vector2(0.36f, 0.075f);    // bottom-centre default box
                rt.anchorMax = new Vector2(0.64f, 0.135f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                _uiRoot = rt;
                _uiCreated = true;
            }

            if (!_uiRegistered)
            {
                _uiRetry += Time.deltaTime;
                if (_uiRetry < 0.5f) return;
                _uiRetry = 0f;
                try
                {
                    if (_uiApiAddMethod == null)
                    {
                        Assembly z = null;
                        Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                        for (int i = 0; i < asms.Length; i++)
                        {
                            Type t = null; try { t = asms[i].GetType("ZeepSDK.UI.UIApi"); } catch { }
                            if (t != null) { z = asms[i]; break; }
                        }
                        if (z == null) return;             // ZeepSDK not present yet -> retry / soft-skip
                        _uiApiAddMethod = z.GetType("ZeepSDK.UI.UIApi")
                            .GetMethod("AddToConfigurator", new Type[] { typeof(RectTransform) });
                    }
                    if (_uiApiAddMethod == null) { _uiRegistered = true; return; } // API shape changed -> stop trying
                    _uiApiAddMethod.Invoke(null, new object[] { _uiRoot });
                    _uiRegistered = true;
                    Logger.LogInfo("Delta Bar: registered with ZeepSDK UI configurator (bar is movable).");
                }
                catch (Exception e) { Logger.LogWarning("DeltaBar: configurator register retry: " + e.Message); }
            }
        }

        // Anchor over Zeepkist's bottom-centre run timer. The HUD has several time
        // displays (lobby time top-right, PB, splits), so rather than guess a field we
        // scan TMP texts for the live run-timer format (MM:SS.mmm) and take the
        // bottom-most match. Returns the timer's horizontal centre (cx) and top edge
        // (topY) in GUI coords (top-left origin); false when not found -> caller centres.
        private bool TryGetLapTimeAnchor(out float cx, out float topY)
        {
            cx = 0f; topY = 0f;

            if (_timerComp == null)
            {
                _anchorRefindTimer += Time.deltaTime;
                if (_anchorRefindTimer < 0.25f) return false;
                _anchorRefindTimer = 0f;
                _timerComp = FindRunTimer();
                if (_timerComp == null) return false;
            }

            RectTransform rt = _timerComp.transform as RectTransform;
            if (rt == null) { _timerComp = null; return false; }
            rt.GetWorldCorners(_corners);                 // overlay canvas: screen px, origin bottom-left
            float l = _corners[0].x, r = _corners[2].x, topScreenY = _corners[1].y;
            cx = (l + r) * 0.5f;
            topY = Screen.height - topScreenY;            // convert to GUI top-origin
            return true;
        }

        // Among all TMP texts, pick the bottom-most one whose text reads MM:SS.mmm.
        private Component FindRunTimer()
        {
            EnsureTmpReflection();
            if (_tmpType == null || _tmpTextProp == null) return null;

            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_tmpType);
            Component best = null;
            float bestScreenY = float.MaxValue;           // smallest screen-y = lowest on screen
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component;
                if (c == null) continue;
                string txt = null;
                try { txt = _tmpTextProp.GetValue(all[i], null) as string; } catch { }
                if (!IsRunTimeFormat(txt)) continue;
                RectTransform rt = c.transform as RectTransform;
                if (rt == null) continue;
                rt.GetWorldCorners(_corners);
                if (_corners[1].y < bestScreenY) { bestScreenY = _corners[1].y; best = c; }
            }
            return best;
        }

        private static bool IsRunTimeFormat(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return Regex.IsMatch(s.Trim(), @"^\d{1,3}:\d{2}\.\d{3}$");
        }

        private void EnsureTmpReflection()
        {
            if (_tmpType != null) return;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type t = null; try { t = asms[i].GetType("TMPro.TMP_Text"); } catch { }
                if (t != null) { _tmpType = t; break; }
            }
            if (_tmpType != null) _tmpTextProp = _tmpType.GetProperty("text");
        }

        private void DrawRect(Rect r, Color c)
        {
            Color o = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = o;
        }

        private string DebugLine()
        {
            if (_show && _p != null && _p.Length >= 2)
                return string.Format("ref={0} frames={1} ct={2:0.00} gt={3:0.00} d={4:+0.00;-0.00;0.00} off={5:0.0}m cur={6}/{7}",
                    _ghostType, _p.Length, _lastCt, _lastGhostT, _delta, _lastDist, _cursor, _p.Length);
            return _status;
        }

        private void EnsureStyles()
        {
            if (_numStyle == null)
            {
                _numStyle = new GUIStyle(GUI.skin.label);
                _numStyle.alignment = TextAnchor.MiddleCenter;
                _numStyle.fontStyle = FontStyle.Bold;
                _numStyle.fontSize = 20;
                _numStyle.normal.textColor = Color.white;
            }
            if (_smallStyle == null)
            {
                _smallStyle = new GUIStyle(GUI.skin.label);
                _smallStyle.fontSize = 12;
                _smallStyle.normal.textColor = Color.yellow;
            }
        }
    }

    // Harmony postfix: fires on every trigger; isFinish==true means the run finished.
    internal static class DeltaBarFinishPatch
    {
        private static void Postfix(bool isFinish)
        {
            if (isFinish && DeltaBarPlugin.Instance != null) DeltaBarPlugin.Instance.OnEditorFinish();
        }
    }

    // Harmony postfix on LevelEditorTrails' TrailManager.LoadTrails: fires whenever the
    // editor loads a level's trails, i.e. on every level change -> clear the stale record.
    internal static class DeltaBarLevelLoadPatch
    {
        private static void Postfix()
        {
            if (DeltaBarPlugin.Instance != null) DeltaBarPlugin.Instance.OnEditorLevelChanged();
        }
    }
}
