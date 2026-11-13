using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Unlimited;

/// <summary>
/// Tracks whether each Harmony patch actually installed, so a patch that
/// silently stops matching cannot ship unnoticed.
///
/// This exists because ChestPatch did exactly that. Game v0.113.0 renamed the
/// field it resolved by string, its setup hit an early return, it logged one
/// Warn line, and the mod went on to report "loaded successfully" — so the
/// chest fix was dead in released builds for weeks with nothing to show for it.
/// A patch that does not install is now an error at load time, named, and
/// counted.
/// </summary>
internal static class PatchRegistry
{
	/// <summary>
	/// Logging indirection, matching the other patches: the game's logger
	/// segfaults outside a game process.
	/// </summary>
	internal static Action<LogLevel, string> LogSink =
		(level, message) => Log.LogMessage(level, LogType.Generic, message);

	private readonly record struct Entry(string Name, bool Installed, bool Required, string Detail);

	private static readonly List<Entry> _entries = new();

	/// <summary>
	/// Records the outcome of one patch installation.
	/// </summary>
	/// <param name="name">Patch target, as a reader would grep for it.</param>
	/// <param name="installed">Whether the patch is now active.</param>
	/// <param name="required">
	/// True if the mod cannot do its job without this one — those turn the load
	/// summary into an error. False for best-effort patches (cosmetic fixes,
	/// optional difficulty targets) whose absence degrades nothing essential.
	/// </param>
	/// <param name="detail">Why it failed, for the summary line.</param>
	public static void Record(string name, bool installed, bool required = true, string detail = null)
	{
		_entries.Add(new Entry(name, installed, required, detail));

		if (installed)
		{
			LogSink(LogLevel.Debug, $"[PatchRegistry] + {name}");
			return;
		}

		LogSink(required ? LogLevel.Error : LogLevel.Warn,
			$"[PatchRegistry] FAILED {name}{(detail != null ? $" — {detail}" : "")}");
	}

	public static void Reset() => _entries.Clear();

	private static IEnumerable<Entry> Failed => _entries.Where(e => !e.Installed);

	public static int RequiredFailures => Failed.Count(e => e.Required);

	/// <summary>
	/// One line describing the state of every patch. Call once, after all
	/// patches have been applied.
	/// </summary>
	public static string Summary()
	{
		int installed = _entries.Count(e => e.Installed);
		var required = Failed.Where(e => e.Required).Select(e => e.Name).ToList();
		var optional = Failed.Where(e => !e.Required).Select(e => e.Name).ToList();

		if (required.Count == 0 && optional.Count == 0)
			return $"all {installed} patches applied";

		var parts = new List<string> { $"{installed}/{_entries.Count} patches applied" };
		if (required.Count > 0) parts.Add($"BROKEN: {string.Join(", ", required)}");
		if (optional.Count > 0) parts.Add($"skipped: {string.Join(", ", optional)}");
		return string.Join("; ", parts);
	}
}
