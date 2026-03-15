using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Unlimited;

public static class ChestPatch
{
    // Cached by RegisterReflectionCache() — called once at mod load
    private static FieldInfo _multiplayerHoldersField;
    private static Type _relicHolderType;

    // Cached RunManager reflection
    private static Type _runManagerType;
    private static PropertyInfo _instanceProp;
    private static PropertyInfo _stateProp;
    private static FieldInfo _stateField;
    private static PropertyInfo _playersListProp;
    private static FieldInfo _playersListField;

    // Set by Prefix, read by Postfix — avoids using holders.Count which may include unused duplicates
    private static int _activePlayers;

    // Cached holder Index setter
    private static MethodInfo _indexSetter;

    private static readonly MethodInfo _getLoopCountMethod =
        typeof(ChestPatch).GetMethod(nameof(GetLoopCount), BindingFlags.Public | BindingFlags.Static);

    /// <summary>
    /// Resolves types and fields once at mod load.
    /// </summary>
    public static void RegisterReflectionCache()
    {
        CacheRunManagerFields();
        CacheRelicCollectionFields();
    }

    private static void CacheRunManagerFields()
    {
        _runManagerType = Type.GetType("MegaCrit.Sts2.Core.Runs.RunManager, sts2", false);
        if (_runManagerType == null) return;

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                               | BindingFlags.Instance | BindingFlags.Static
                               | BindingFlags.FlattenHierarchy;

        _instanceProp = _runManagerType.GetProperty("Instance", all);

        // Try property first, then field for State
        _stateProp = _runManagerType.GetProperty("State", all);
        if (_stateProp == null)
            _stateField = _runManagerType.GetField("State", all)
                       ?? _runManagerType.GetField("_state", all)
                       ?? _runManagerType.GetField("_runState", all);

        // Resolve Players from the State type
        var stateType = _stateProp?.PropertyType ?? _stateField?.FieldType;
        if (stateType != null)
        {
            _playersListProp = stateType.GetProperty("Players", all);
            if (_playersListProp == null)
                _playersListField = stateType.GetField("Players", all)
                                 ?? stateType.GetField("_players", all);
        }

        // Log all State-like members to help diagnose when things go wrong
        var stateMembers = string.Join(", ", _runManagerType
            .GetMembers(all)
            .Where(m => m.Name.ToLower().Contains("state") || m.Name.ToLower().Contains("player"))
            .Select(m => m.Name));
        Log.LogMessage(LogLevel.Info, LogType.Generic,
            $"[ChestPatch] RunManager State/Player members: {stateMembers}");

        Log.LogMessage(LogLevel.Info, LogType.Generic,
            $"[ChestPatch] RunManager cache: Instance={_instanceProp?.Name ?? "null"}, " +
            $"State={_stateProp?.Name ?? _stateField?.Name ?? "null"}, " +
            $"Players={_playersListProp?.Name ?? _playersListField?.Name ?? "null"}");
    }

    private static void CacheRelicCollectionFields()
    {
        var collectionType = Type.GetType(
            "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection, sts2", false);

        if (collectionType == null)
        {
            // Fallback: search all loaded assemblies by simple name
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                collectionType = asm.GetType("MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection");
                if (collectionType != null) break;
            }
        }

        if (collectionType == null)
        {
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                "[ChestPatch] NTreasureRoomRelicCollection not found — chest patch will be skipped");
            return;
        }

        _multiplayerHoldersField = collectionType.GetField("_multiplayerHolders",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (_multiplayerHoldersField == null)
        {
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                "[ChestPatch] _multiplayerHolders field not found on NTreasureRoomRelicCollection");
            return;
        }

        var genericArgs = _multiplayerHoldersField.FieldType.GetGenericArguments();
        _relicHolderType = genericArgs.Length > 0 ? genericArgs[0] : null;

        _indexSetter = _relicHolderType?.GetMethod("set_Index",
            BindingFlags.Public | BindingFlags.Instance);

        Log.LogMessage(LogLevel.Info, LogType.Generic,
            $"[ChestPatch] _multiplayerHolders: {_multiplayerHoldersField.FieldType.Name}, " +
            $"holder type: {_relicHolderType?.Name ?? "unknown"}, " +
            $"set_Index: {(_indexSetter != null ? "found" : "null")}");
    }

    public static FieldInfo GetMultiplayerHoldersField() => _multiplayerHoldersField;

    public static MethodInfo GetInitializeRelicsMethod()
    {
        if (_multiplayerHoldersField == null) return null;
        return _multiplayerHoldersField.DeclaringType?.GetMethod(
            "InitializeRelics",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    /// <summary>
    /// Returns the actual number of players in the current run.
    /// Used as the loop bound in InitializeRelics so every player gets a relic holder.
    /// </summary>
    public static int GetLoopCount()
    {
        try
        {
            if (_instanceProp == null) return Sts2Unlimited.MaxPlayersOverride;

            var rm = _instanceProp.GetValue(null);
            if (rm == null) return Sts2Unlimited.MaxPlayersOverride;

            var state = _stateProp?.GetValue(rm) ?? _stateField?.GetValue(rm);
            if (state == null) return Sts2Unlimited.MaxPlayersOverride;

            var players = _playersListProp?.GetValue(state) ?? _playersListField?.GetValue(state);
            if (players == null) return Sts2Unlimited.MaxPlayersOverride;

            int count = (int)players.GetType().GetProperty("Count")!.GetValue(players)!;
            Log.LogMessage(LogLevel.Debug, LogType.Generic, $"[ChestPatch] GetLoopCount() = {count}");
            return count;
        }
        catch (Exception e)
        {
            Log.LogMessage(LogLevel.Warn, LogType.Generic, $"[ChestPatch] GetLoopCount() failed: {e.Message}");
            return Sts2Unlimited.MaxPlayersOverride;
        }
    }

    /// <summary>
    /// Runs before InitializeRelics. Duplicates the last holder node for each extra player
    /// so the transpiler's extended loop has a real node per player.
    /// </summary>
    public static void Prefix_InitializeRelics(object __instance)
    {
        try
        {
            if (_multiplayerHoldersField == null) return;

            var holders = _multiplayerHoldersField.GetValue(__instance) as System.Collections.IList;
            if (holders == null || holders.Count == 0) return;

            int numPlayers = GetLoopCount();
            _activePlayers = numPlayers; // save for postfix

            var lastHolder = holders[holders.Count - 1] as Godot.Node;
            if (lastHolder == null) return;

            var parent = lastHolder.GetParent();
            if (parent == null)
            {
                Log.LogMessage(LogLevel.Warn, LogType.Generic,
                    "[ChestPatch] Last holder has no parent — cannot duplicate holders");
                return;
            }

            // Duplicate extra holders if needed.
            int startIndex = holders.Count;
            for (int i = startIndex; i < numPlayers; i++)
            {
                var duplicate = lastHolder.Duplicate();
                if (duplicate == null) continue;
                parent.AddChild(duplicate);
                _indexSetter?.Invoke(duplicate, new object[] { i });
                holders.Add(duplicate);
            }

            Log.LogMessage(LogLevel.Info, LogType.Generic,
                $"[ChestPatch] Prefix: added {holders.Count - startIndex} holder(s), total={holders.Count}");
        }
        catch (Exception e)
        {
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                $"[ChestPatch] Prefix_InitializeRelics failed: {e.Message}");
        }
    }

    private static Godot.Vector2 GetNodePosition(object node)
    {
        if (node is Godot.Control c) return c.Position;
        if (node is Godot.Node2D n) return n.Position;
        return Godot.Vector2.Zero;
    }

    private static void SetNodePosition(object node, Godot.Vector2 pos)
    {
        if (node is Godot.Control c) { c.Position = pos; return; }
        if (node is Godot.Node2D n) { n.Position = pos; return; }
        // Last resort: use .NET reflection (handles GDExtension-wrapped types)
        node.GetType().GetProperty("Position")?.SetValue(node, pos);
    }

    private static Godot.Vector2 GetNodeSize(object node)
    {
        if (node is Godot.Control c) return c.Size;
        return new Godot.Vector2(100f, 100f);
    }

    /// <summary>
    /// Runs after InitializeRelics. Repositions all holders in an evenly-spaced circle
    /// so the game's default positioning doesn't stick.
    /// </summary>
    public static void Postfix_InitializeRelics(object __instance)
    {
        try
        {
            if (_multiplayerHoldersField == null) return;

            var holders = _multiplayerHoldersField.GetValue(__instance) as System.Collections.IList;
            if (holders == null || holders.Count == 0) return;

            // Use the player count saved by the prefix, not holders.Count which may include
            // unused duplicates added to satisfy MaxPlayersOverride.
            int numPlayers = _activePlayers > 0 ? _activePlayers : GetLoopCount();
            if (numPlayers <= 0 || numPlayers > holders.Count) numPlayers = holders.Count;
            var holderSize = GetNodeSize(holders[0]);

            // Determine center and radius from parent, falling back to average of holder positions.
            var parentNode = (holders[0] as Godot.Node)?.GetParent();
            var parentCtrl = parentNode as Godot.Control;

            Godot.Vector2 center;
            float radius;

            if (parentCtrl != null && (parentCtrl.Size.X > 1f || parentCtrl.Size.Y > 1f))
            {
                center = parentCtrl.Size / 2f;
                radius = Math.Min(parentCtrl.Size.X, parentCtrl.Size.Y) * 0.35f;
                center.Y += radius * 0.15f;
            }
            else
            {
                // Fall back: center on the average position of current holders.
                Godot.Vector2 sum = Godot.Vector2.Zero;
                for (int i = 0; i < numPlayers; i++)
                    sum += GetNodePosition(holders[i]);
                center = sum / numPlayers + holderSize / 2f;
                radius = 200f;
            }

            Log.LogMessage(LogLevel.Info, LogType.Generic,
                $"[ChestPatch] Circle layout: center={center}, radius={radius}, n={numPlayers}, " +
                $"holderType={holders[0]?.GetType().Name ?? "null"}, parentType={parentNode?.GetType().Name ?? "null"}");

            // Position all holders evenly around the circle, starting at the top (−90°).
            for (int i = 0; i < numPlayers; i++)
            {
                float angle = -MathF.PI / 2f + 2f * MathF.PI * i / numPlayers;
                var pos = center
                    + new Godot.Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius
                    - holderSize / 2f;
                SetNodePosition(holders[i], pos);
            }

            Log.LogMessage(LogLevel.Info, LogType.Generic,
                $"[ChestPatch] Postfix: arranged {numPlayers} holders in circle");
        }
        catch (Exception e)
        {
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                $"[ChestPatch] Postfix_InitializeRelics failed: {e.Message}\n{e.StackTrace}");
        }
    }

    public static IEnumerable<CodeInstruction> Transpile_InitializeRelics(
        IEnumerable<CodeInstruction> instructions)
    {
        if (_multiplayerHoldersField == null)
        {
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                "[ChestPatch] Reflection cache not ready — InitializeRelics transpiler skipped");
            return instructions;
        }

        var codes = new List<CodeInstruction>(instructions);
        var getCountMethod = _multiplayerHoldersField.FieldType.GetMethod("get_Count");

        // Replace loop upper-bound: _multiplayerHolders.Count → GetLoopCount()
        //
        // Target IL:
        //   ldfld  _multiplayerHolders   ← codes[i]
        //   callvirt get_Count           ← codes[i+1]
        //
        // Replacement:
        //   pop                          ← discard the 'this' ldfld would have consumed
        //   call GetLoopCount            ← pushes actual player count
        for (int i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode == OpCodes.Ldfld &&
                codes[i].operand is FieldInfo f && f == _multiplayerHoldersField &&
                codes[i + 1].opcode == OpCodes.Callvirt &&
                codes[i + 1].operand is MethodInfo m && m == getCountMethod)
            {
                codes[i]     = new CodeInstruction(OpCodes.Pop);
                codes[i + 1] = new CodeInstruction(OpCodes.Call, _getLoopCountMethod);
                Log.LogMessage(LogLevel.Info, LogType.Generic,
                    "[ChestPatch] Patched loop bound in InitializeRelics");
                return codes;
            }
        }

        Log.LogMessage(LogLevel.Warn, LogType.Generic,
            "[ChestPatch] Loop bound pattern not found in InitializeRelics — deadlock fix may be ineffective");
        return codes;
    }
}
