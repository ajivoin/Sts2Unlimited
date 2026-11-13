using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Unlimited;

/// <summary>
/// Keeps the shared treasure chest usable when there are more relics than the
/// game has holders for.
///
/// Game v0.113.0 redesigned this screen. It used to grow a single
/// _multiplayerHolders list at runtime; now treasure_room.tscn ships six
/// pre-built layouts (Container/Layout1..Layout6, holding 1..6 relic holders)
/// and InitializeRelics picks the one whose index == relicCount - 1:
///
///     for (int i = 0; i &lt; _holderLayouts.Count; i++)
///         if (i == currentRelics.Count - 1) { show it; _holdersInUse = its children; }
///         else hide it;
///
/// A 7th relic therefore matches no layout at all: every layout stays hidden,
/// _holdersInUse stays empty, no holder is ever clickable, and the run hangs
/// with every player waiting on a vote that cannot be cast. Seven relics is
/// reachable at 7+ players in standard mode, and at 5+ players in Blitz (which
/// generates players + 2).
///
/// The fix is two halves and needs both:
///
///   1. A transpiler clamps the layout choice to the largest available layout,
///      so Layout6 is selected instead of none.
///   2. A postfix tops _holdersInUse up to one holder per relic, by borrowing
///      the idle holders sitting in the five now-hidden layouts (15 of them,
///      so this covers up to 21 relics).
///
/// Clamping alone is not enough. AwardRelics emits ConsolationPrize/Skipped
/// results for relic indices nobody voted for, and AnimateRelicAwards does
/// _holdersInUse[result.relicIndex] — so relic index 6 against six holders
/// throws inside the award animation and the run hangs anyway, just later.
///
/// Everything here binds to the game's types at compile time on purpose. The
/// previous version of this patch resolved NTreasureRoomRelicCollection and
/// its fields from strings, so when v0.113.0 renamed them the patch quietly
/// stopped installing and shipped broken for weeks. Referencing HoldersInUse
/// (added in v0.113.0) means a build against an older sts2.dll now fails to
/// compile instead.
/// </summary>
public static class ChestPatch
{
	/// <summary>Widest ring we lay holders out on, in pixels, before crowding forces it wider.</summary>
	private const float RingRadiusX = 380f;

	/// <summary>Ring is wider than it is tall — the chest screen has far more horizontal room.</summary>
	private const float RingRadiusY = 200f;

	/// <summary>Holders are 136x136 in treasure_room.tscn; leave a little air between them.</summary>
	private const float HolderSpacing = 150f;

	/// <summary>
	/// Logging indirection. The game's logger calls into Godot natives and
	/// segfaults outside a game process, so the headless test harness swaps
	/// this for a capturing sink.
	/// </summary>
	internal static Action<LogLevel, string> LogSink =
		(level, message) => Log.LogMessage(level, LogType.Generic, message);

	private static FieldInfo _holderLayoutsField;
	private static FieldInfo _holdersInUseField;
	private static FieldInfo _runStateField;
	private static FieldInfo _currentRelicsField;
	private static MethodInfo _pickRelicMethod;
	private static MethodInfo _refreshNavigationMethod;

	private static readonly MethodInfo _clampMethod =
		typeof(ChestPatch).GetMethod(nameof(ClampToLayoutCount), BindingFlags.Public | BindingFlags.Static);

	/// <summary>True once every private member this patch reaches for has been located.</summary>
	public static bool Ready { get; private set; }

	/// <summary>
	/// Resolves the handful of private members the patch needs. Public API is
	/// bound at compile time; these are the members C# cannot reach directly.
	/// Returns false (and says which member was missing) rather than letting a
	/// half-initialized patch install.
	/// </summary>
	public static bool TryInitialize()
	{
		var collection = typeof(NTreasureRoomRelicCollection);

		_holderLayoutsField = AccessTools.Field(collection, "_holderLayouts");
		_holdersInUseField = AccessTools.Field(collection, "_holdersInUse");
		_runStateField = AccessTools.Field(collection, "_runState");
		_pickRelicMethod = AccessTools.Method(collection, "PickRelic", new[] { typeof(NTreasureRoomRelicHolder) });
		_refreshNavigationMethod = AccessTools.Method(collection, "RefreshNavigation");
		_currentRelicsField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_currentRelics");

		var missing = new List<string>();
		if (_holderLayoutsField == null) missing.Add("NTreasureRoomRelicCollection._holderLayouts");
		if (_holdersInUseField == null) missing.Add("NTreasureRoomRelicCollection._holdersInUse");
		if (_runStateField == null) missing.Add("NTreasureRoomRelicCollection._runState");
		if (_pickRelicMethod == null) missing.Add("NTreasureRoomRelicCollection.PickRelic");
		if (_refreshNavigationMethod == null) missing.Add("NTreasureRoomRelicCollection.RefreshNavigation");
		if (_currentRelicsField == null) missing.Add("TreasureRoomRelicSynchronizer._currentRelics");

		if (missing.Count > 0)
		{
			LogSink(LogLevel.Error,
				$"[ChestPatch] Cannot install — missing game members: {string.Join(", ", missing)}");
			Ready = false;
			return false;
		}

		Ready = true;
		return true;
	}

	public static MethodInfo GetInitializeRelicsMethod() =>
		AccessTools.Method(typeof(NTreasureRoomRelicCollection), nameof(NTreasureRoomRelicCollection.InitializeRelics));

	/// <summary>
	/// Called from the transpiled InitializeRelics in place of
	/// currentRelics.Count, so the layout search looks for an index that
	/// actually exists. Returns the relic count unchanged in every case the
	/// base game already handles.
	/// </summary>
	public static int ClampToLayoutCount(int relicCount, NTreasureRoomRelicCollection collection)
	{
		try
		{
			var layouts = _holderLayoutsField?.GetValue(collection) as List<Control>;
			if (layouts == null || layouts.Count == 0) return relicCount;
			if (relicCount <= layouts.Count) return relicCount;

			// Debug, not Info: the transpiled site sits inside the layout loop, so
			// this runs once per layout. The postfix's one-line summary below is
			// the signal worth reading.
			LogSink(LogLevel.Debug,
				$"[ChestPatch] {relicCount} relics but only {layouts.Count} layouts — using the largest");
			return layouts.Count;
		}
		catch (Exception e)
		{
			LogSink(LogLevel.Warn,
				$"[ChestPatch] ClampToLayoutCount failed: {e.Message}");
			return relicCount;
		}
	}

	/// <summary>
	/// Rewrites the layout-selection test in InitializeRelics from
	///     i == currentRelics.Count - 1
	/// to
	///     i == ClampToLayoutCount(currentRelics.Count, this) - 1
	/// by inserting ldarg.0 + call immediately after the get_Count.
	///
	/// The match is the four-instruction sequence get_Count / ldc.i4.1 / sub /
	/// bne.un, which occurs exactly once in the method — the other two
	/// get_Count calls are loop bounds followed by blt, and the empty-chest
	/// test is followed by brtrue. Anything other than exactly one match means
	/// the method changed shape again, so the patch reports failure instead of
	/// guessing.
	/// </summary>
	public static IEnumerable<CodeInstruction> Transpile_InitializeRelics(
		IEnumerable<CodeInstruction> instructions)
	{
		var codes = new List<CodeInstruction>(instructions);

		int matchIndex = -1;
		int matches = 0;

		for (int i = 0; i < codes.Count - 3; i++)
		{
			if (codes[i].opcode != OpCodes.Callvirt) continue;
			if (codes[i].operand is not MethodInfo m || m.Name != "get_Count") continue;
			if (codes[i + 1].opcode != OpCodes.Ldc_I4_1) continue;
			if (codes[i + 2].opcode != OpCodes.Sub) continue;
			if (codes[i + 3].opcode != OpCodes.Bne_Un && codes[i + 3].opcode != OpCodes.Bne_Un_S) continue;

			matches++;
			matchIndex = i;
		}

		if (matches != 1)
		{
			LogSink(LogLevel.Error,
				$"[ChestPatch] Expected exactly 1 layout-selection site in InitializeRelics, found {matches} " +
				"— leaving the method unpatched; chests will hang above 6 relics");
			TranspilerApplied = false;
			return codes;
		}

		var loadThis = new CodeInstruction(OpCodes.Ldarg_0);

		// A branch landing on the ldc.i4.1 has to run the clamp too, so any
		// labels on it move to the first instruction we insert.
		loadThis.labels.AddRange(codes[matchIndex + 1].labels);
		codes[matchIndex + 1].labels.Clear();

		codes.InsertRange(matchIndex + 1, new[]
		{
			loadThis,
			new CodeInstruction(OpCodes.Call, _clampMethod),
		});

		LogSink(LogLevel.Info, "[ChestPatch] Clamped layout selection in InitializeRelics");
		TranspilerApplied = true;
		return codes;
	}

	/// <summary>Whether the transpiler found its site. Reported in the load summary.</summary>
	public static bool TranspilerApplied { get; private set; }

	/// <summary>
	/// Tops the active layout up to one holder per relic. Runs after the base
	/// method, so the layout is already chosen, visible, and populated with
	/// however many holders it shipped with.
	/// </summary>
	public static void Postfix_InitializeRelics(NTreasureRoomRelicCollection __instance)
	{
		if (!Ready) return;

		var synchronizer = RunManager.Instance?.TreasureRoomRelicSynchronizer;
		var relics = synchronizer?.CurrentRelics;
		if (relics == null || relics.Count == 0) return;

		var holders = _holdersInUseField.GetValue(__instance) as List<NTreasureRoomRelicHolder>;
		if (holders == null || holders.Count == 0) return;
		if (relics.Count <= holders.Count) return;

		try
		{
			AddMissingHolders(__instance, holders, relics);
		}
		catch (Exception e)
		{
			LogSink(LogLevel.Error,
				$"[ChestPatch] Could not add holders for {relics.Count} relics: {e.Message}");

			// Last resort: shrink the relic list to what the screen can show.
			// Fewer relics on offer is a worse chest, but it is not a hung run.
			TrimRelicsToHolderCount(synchronizer, holders.Count, relics.Count);
		}
	}

	private static void AddMissingHolders(
		NTreasureRoomRelicCollection collection,
		List<NTreasureRoomRelicHolder> holders,
		IReadOnlyList<RelicModel> relics)
	{
		var activeLayout = holders[holders.Count - 1].GetParent();
		if (activeLayout == null)
			throw new InvalidOperationException("active layout holder has no parent");

		var spares = CollectIdleHolders(collection, activeLayout);
		int needed = relics.Count - holders.Count;
		if (spares.Count < needed)
			throw new InvalidOperationException(
				$"need {needed} more holders but only {spares.Count} are idle");

		var runState = (IRunState)_runStateField.GetValue(collection);
		var onReleased = Callable.From(
			(Action<NTreasureRoomRelicHolder>)Delegate.CreateDelegate(
				typeof(Action<NTreasureRoomRelicHolder>), collection, _pickRelicMethod));

		// Everything from here on is a holder the base game did not lay out, so
		// holders[0] stays a clean reference to compare the new ones against.
		int firstBorrowed = holders.Count;
		var reference = holders[0];

		for (int i = 0; i < needed; i++)
		{
			var holder = spares[i];
			holder.GetParent()?.RemoveChild(holder);
			activeLayout.AddChild(holder);

			int index = holders.Count;
			holder.Initialize(relics[index], runState);
			holder.Index = index;

			// The base game ignores this return value. We cannot afford to: a holder
			// that is visible but not wired to PickRelic is unclickable, which is the
			// exact hang this patch exists to prevent, with no exception to notice.
			// Throwing hands over to the trim fallback -- a smaller chest, not a
			// stuck one.
			var connected = holder.Connect(NClickableControl.SignalName.Released, onReleased);
			if (connected != Error.Ok)
				throw new InvalidOperationException(
					$"connecting holder {index} to PickRelic returned {connected}");

			holder.VoteContainer.RefreshPlayerVotes();
			holders.Add(holder);
		}

		ArrangeInRing(holders, activeLayout as Control);
		_refreshNavigationMethod.Invoke(collection, null);

		LogSink(LogLevel.Info,
			$"[ChestPatch] Extended chest to {collection.HoldersInUse.Count} holders for {relics.Count} relics");

		ReportHolderState(holders, firstBorrowed, reference);
	}

	/// <summary>
	/// Logs where every holder ended up, and flags any borrowed one that differs from
	/// a holder the base game placed itself in a way that would stop a player clicking
	/// it.
	///
	/// The comparison is deliberately differential rather than absolute. The obvious
	/// check -- `IsVisibleInTree()` -- is useless here: `AnimIn()` makes the collection
	/// visible only *after* `InitializeRelics` returns, so at this point every holder
	/// reports invisible, the base game's own included. Comparing against a sibling the
	/// base game laid out is immune to that ordering, and to any later change in it.
	///
	/// This is the patch's only field diagnostic. Its body cannot be exercised
	/// headlessly -- it needs live Godot nodes -- so when something does go wrong here,
	/// this log line is what there is to go on.
	/// </summary>
	private static void ReportHolderState(
		List<NTreasureRoomRelicHolder> holders, int firstBorrowed, NTreasureRoomRelicHolder reference)
	{
		try
		{
			LogSink(LogLevel.Info, "[ChestPatch] Holders: " + string.Join("  ", holders.Select(
				(h, i) => $"#{i}{(i >= firstBorrowed ? "*" : "")}@{h.OffsetLeft:0},{h.OffsetTop:0} {h.Size.X:0}x{h.Size.Y:0}")));

			var problems = new List<string>();
			for (int i = firstBorrowed; i < holders.Count; i++)
			{
				var holder = holders[i];
				if (!holder.IsInsideTree())
					problems.Add($"#{i} is not in the scene tree");
				if (holder.Visible != reference.Visible)
					problems.Add($"#{i} Visible={holder.Visible} but the base game's holders are {reference.Visible}");
				if (holder.GetParent() != reference.GetParent())
					problems.Add($"#{i} is parented to {holder.GetParent()?.Name}, not {reference.GetParent()?.Name}");
				if (holder.VoteContainer == null)
					problems.Add($"#{i} has no VoteContainer");
			}

			if (problems.Count > 0)
				LogSink(LogLevel.Error,
					$"[ChestPatch] {problems.Count} borrowed holder(s) may not be clickable: {string.Join("; ", problems)}");
		}
		catch (Exception e)
		{
			// Diagnostics must never be the thing that breaks the chest.
			LogSink(LogLevel.Warn, $"[ChestPatch] Could not report holder state: {e.Message}");
		}
	}

	/// <summary>
	/// Every relic holder sitting in a layout the base game just hid. These are
	/// fully instanced from the same scene resource as the active ones and have
	/// no runtime signal connections, which makes them safer to move than a
	/// Duplicate() — Node.Duplicate copies signal connections by default and
	/// would double-wire PickRelic.
	/// </summary>
	private static List<NTreasureRoomRelicHolder> CollectIdleHolders(
		NTreasureRoomRelicCollection collection, Node activeLayout)
	{
		var layouts = _holderLayoutsField.GetValue(collection) as List<Control>;
		var idle = new List<NTreasureRoomRelicHolder>();
		if (layouts == null) return idle;

		// Largest layouts first, so a 7-relic chest robs Layout5 rather than
		// stripping the small ones and leaving an odd spread behind.
		foreach (var layout in layouts.AsEnumerable().Reverse())
		{
			if (layout == null || layout == activeLayout) continue;
			idle.AddRange(layout.GetChildren().OfType<NTreasureRoomRelicHolder>());
		}

		return idle;
	}

	/// <summary>
	/// Lays all holders out on an ellipse centred on the layout. The shipped
	/// Layout1..Layout6 arrangements are hand-placed and only go up to six, so
	/// once we are adding holders we take over positioning entirely.
	/// </summary>
	private static void ArrangeInRing(List<NTreasureRoomRelicHolder> holders, Control layout)
	{
		int n = holders.Count;
		if (n == 0) return;

		// Widen the ring until neighbours stop overlapping: the chord between
		// two adjacent points on a circle of radius r is 2*r*sin(pi/n).
		float minRadius = HolderSpacing / (2f * MathF.Sin(MathF.PI / Math.Max(n, 3)));
		float scale = Math.Max(1f, minRadius / RingRadiusY);

		float radiusX = RingRadiusX * scale;
		float radiusY = RingRadiusY * scale;

		// Do not push holders off the edge of the screen.
		if (layout != null && layout.Size.X > 1f && layout.Size.Y > 1f)
		{
			radiusX = Math.Min(radiusX, layout.Size.X * 0.5f - HolderSpacing * 0.5f);
			radiusY = Math.Min(radiusY, layout.Size.Y * 0.5f - HolderSpacing * 0.5f);
		}

		for (int i = 0; i < n; i++)
		{
			float angle = -MathF.PI / 2f + 2f * MathF.PI * i / n;
			float cx = MathF.Cos(angle) * radiusX;
			float cy = MathF.Sin(angle) * radiusY;

			// Holders are anchored to the layout's centre (anchors_preset 8),
			// so their offsets are already relative to the point we want.
			var holder = holders[i];
			float half = holder.Size.X > 1f ? holder.Size.X * 0.5f : HolderSpacing * 0.5f;
			float halfY = holder.Size.Y > 1f ? holder.Size.Y * 0.5f : HolderSpacing * 0.5f;

			holder.OffsetLeft = cx - half;
			holder.OffsetRight = cx + half;
			holder.OffsetTop = cy - halfY;
			holder.OffsetBottom = cy + halfY;
		}
	}

	/// <summary>
	/// Drops relics the screen has no holder for. Only reached if adding
	/// holders failed; without it AnimateRelicAwards would index
	/// _holdersInUse past its end and hang the run.
	/// </summary>
	private static void TrimRelicsToHolderCount(
		TreasureRoomRelicSynchronizer synchronizer, int holderCount, int relicCount)
	{
		try
		{
			if (_currentRelicsField.GetValue(synchronizer) is not List<RelicModel> current) return;
			if (current.Count <= holderCount) return;

			current.RemoveRange(holderCount, current.Count - holderCount);
			LogSink(LogLevel.Warn,
				$"[ChestPatch] Fell back to trimming {relicCount} relics down to {holderCount}");
		}
		catch (Exception e)
		{
			LogSink(LogLevel.Error,
				$"[ChestPatch] Relic trim fallback failed too: {e.Message} — the chest may hang");
		}
	}
}
