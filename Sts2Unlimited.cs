using Godot;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Logging;
using HarmonyLib;

namespace Sts2Unlimited;

[ModInitializer("ModLoaded")]
public static class Sts2Unlimited
{
	/// <summary>
	/// Maximum number of players to allow in a lobby. Defaults to 8 but
	/// can be overridden by a text config file located next to the mod DLL.
	/// </summary>
	private static int maxPlayersOverride = 8;

	private static Harmony harmony;

	public static int MaxPlayersOverride { get => maxPlayersOverride; set => maxPlayersOverride = value; }

	private static bool difficultyOverrideEnabled = false;
	private static int  difficultyPlayersOverride  = 4;
	private static bool difficultyScaleSingleplayerEnabled = false;

	public static bool DifficultyOverrideEnabled { get => difficultyOverrideEnabled; set => difficultyOverrideEnabled = value; }
	public static int  DifficultyPlayersOverride  { get => difficultyPlayersOverride; set => difficultyPlayersOverride = value; }
	public static bool DifficultyScaleSingleplayerEnabled { get => difficultyScaleSingleplayerEnabled; set => difficultyScaleSingleplayerEnabled = value; }

	public static int GetEffectivePlayerCount(int actualCount)
	{
		if (!DifficultyOverrideEnabled) return actualCount;
		if (actualCount == 1 && !DifficultyScaleSingleplayerEnabled) return actualCount;
		return DifficultyPlayersOverride;
	}

	public static void ModLoaded()
	{
		LoadConfig();
		ApplyHarmonyPatches();
		SettingsMenuIntegration.InitializeSettingsMenuUI(harmony);
		Log.LogMessage(LogLevel.Info, LogType.Generic, $"Sts2Unlimited loaded successfully! maxPlayers set to {MaxPlayersOverride}");
	}

	private static void LoadConfig()
	{
		try
		{
			string dllDir = Path.GetDirectoryName(typeof(Sts2Unlimited).Assembly.Location) ?? ".";

			// Try JSON settings first
			string jsonPath = Path.Combine(dllDir, "sts2unlimited.settings.json");
			bool maxPlayersLoadedFromJson = false;
			if (File.Exists(jsonPath))
			{
				string json = File.ReadAllText(jsonPath);
				var doc = JsonDocument.Parse(json);
				if (doc.RootElement.TryGetProperty("MaxPlayers", out var val))
				{
					MaxPlayersOverride = val.GetInt32();
					maxPlayersLoadedFromJson = true;
				}
				if (doc.RootElement.TryGetProperty("DifficultyEnabled", out var de))
					DifficultyOverrideEnabled = de.GetBoolean();
				if (doc.RootElement.TryGetProperty("DifficultyPlayers", out var dp))
					DifficultyPlayersOverride = Math.Clamp(dp.GetInt32(),
						SettingsMenuIntegration.DIFFICULTY_MIN, SettingsMenuIntegration.DIFFICULTY_MAX);
				if (doc.RootElement.TryGetProperty("DifficultySingleplayer", out var dsp))
					DifficultyScaleSingleplayerEnabled = dsp.GetBoolean();
			}

			// Fall back to legacy text file — only ever stored MaxPlayers (predates the JSON
			// format and the difficulty fields), so this fallback only applies to that one value.
			// Runs whenever MaxPlayers wasn't loaded from JSON, whether because settings.json
			// doesn't exist at all or because it exists but is missing/malformed that key.
			if (!maxPlayersLoadedFromJson)
			{
				string txtPath = Path.Combine(dllDir, "sts2unlimited.maxplayers.txt");
				if (File.Exists(txtPath) && int.TryParse(File.ReadAllText(txtPath).Trim(), out var n) && n > 0)
					MaxPlayersOverride = n;
			}
		}
		catch (Exception e)
		{
			Log.LogMessage(LogLevel.Error, LogType.Generic, $"Unable to read max-player config: {e.Message}");
		}
	}

	private static void ApplyHarmonyPatches()
	{
		try
		{
			harmony = new Harmony("sts2unlimited.modifier");

			// Patch StartSteamHost to use custom player count
			var steamHostMethod = typeof(MegaCrit.Sts2.Core.Multiplayer.NetHostGameService).GetMethod(
				"StartSteamHost",
				BindingFlags.Public | BindingFlags.Instance,
				null,
				[typeof(int)],
				null
			);

			var netHostType = typeof(MegaCrit.Sts2.Core.Multiplayer.NetHostGameService);
			var netHostMethodList = string.Join("; ", netHostType
				.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})"));

			if (steamHostMethod != null)
			{
				var steamPatch = typeof(Sts2Unlimited).GetMethod(nameof(Patch_StartSteamHost), BindingFlags.NonPublic | BindingFlags.Static);
				harmony.Patch(steamHostMethod, prefix: new HarmonyMethod(steamPatch));
				Log.LogMessage(LogLevel.Info, LogType.Generic, "[HostPatch] Patched StartSteamHost");
			}
			else
			{
				Log.LogMessage(LogLevel.Warn, LogType.Generic,
					$"[HostPatch] StartSteamHost(int) NOT FOUND on NetHostGameService. Members: {netHostMethodList}");
			}

			// Patch StartENetHost to use custom player count
			var enetHostMethod = typeof(MegaCrit.Sts2.Core.Multiplayer.NetHostGameService).GetMethod(
				"StartENetHost",
				BindingFlags.Public | BindingFlags.Instance,
				null,
				[typeof(ushort), typeof(int)],
				null
			);

			if (enetHostMethod != null)
			{
				var enetPatch = typeof(Sts2Unlimited).GetMethod(nameof(Patch_StartENetHost), BindingFlags.NonPublic | BindingFlags.Static);
				harmony.Patch(enetHostMethod, prefix: new HarmonyMethod(enetPatch));
				Log.LogMessage(LogLevel.Info, LogType.Generic, "[HostPatch] Patched StartENetHost");
			}
			else
			{
				Log.LogMessage(LogLevel.Warn, LogType.Generic,
					$"[HostPatch] StartENetHost(UInt16, int) NOT FOUND on NetHostGameService. Members: {netHostMethodList}");
			}

			// Patch NCharacterSelectScreen.InitializeMultiplayerAsHost — this is what actually
			// governs how many players StartRunLobby will seat (see TryAddPlayerInFirstAvailableSlot,
			// which loops slot indices only up to MaxPlayers). StartSteamHost/StartENetHost's
			// maxClients only controls the transport-level accept limit (Steam lobby member count /
			// ENet socket cap); leaving this one at vanilla's hardcoded 4 meant the 5th+ player could
			// connect at the network level but still get refused a run slot. Previously removed in
			// b215ae7 on a since-unconfirmed theory that it caused a partial-lobby black screen —
			// IsAboutToBeginGame/BeginRunForAllPlayersIfAllReady only wait on the players actually
			// present in StartRunLobby.Players, not on MaxPlayers, so that shouldn't reproduce here.
			var charSelectType = typeof(MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen);
			var charInitMethod = charSelectType.GetMethod(
				"InitializeMultiplayerAsHost",
				BindingFlags.Public | BindingFlags.Instance,
				null,
				[typeof(INetGameService), typeof(int)],
				null
			);

			if (charInitMethod != null)
			{
				var charPatch = typeof(Sts2Unlimited).GetMethod(nameof(Patch_LobbyInitMaxPlayers), BindingFlags.NonPublic | BindingFlags.Static);
				harmony.Patch(charInitMethod, prefix: new HarmonyMethod(charPatch));
				Log.LogMessage(LogLevel.Info, LogType.Generic, "[HostPatch] Patched NCharacterSelectScreen.InitializeMultiplayerAsHost");
			}
			else
			{
				Log.LogMessage(LogLevel.Warn, LogType.Generic,
					"[HostPatch] NCharacterSelectScreen.InitializeMultiplayerAsHost(INetGameService, int) NOT FOUND.");
			}

			// Patch NCustomRunScreen.InitializeMultiplayerAsHost — same reasoning as above, for the
			// Custom Run host flow.
			var customRunType = typeof(MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen);
			var customInitMethod = customRunType.GetMethod(
				"InitializeMultiplayerAsHost",
				BindingFlags.Public | BindingFlags.Instance,
				null,
				[typeof(INetGameService), typeof(int)],
				null
			);

			if (customInitMethod != null)
			{
				var customPatch = typeof(Sts2Unlimited).GetMethod(nameof(Patch_LobbyInitMaxPlayers), BindingFlags.NonPublic | BindingFlags.Static);
				harmony.Patch(customInitMethod, prefix: new HarmonyMethod(customPatch));
				Log.LogMessage(LogLevel.Info, LogType.Generic, "[HostPatch] Patched NCustomRunScreen.InitializeMultiplayerAsHost");
			}
			else
			{
				Log.LogMessage(LogLevel.Warn, LogType.Generic,
					"[HostPatch] NCustomRunScreen.InitializeMultiplayerAsHost(INetGameService, int) NOT FOUND.");
			}

			// Patch NRestSiteRoom._Ready to handle >4 players at the campfire
			var restSiteRoomType = typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom);
			var restSiteReadyMethod = restSiteRoomType.GetMethod(
				"_Ready",
				BindingFlags.Public | BindingFlags.Instance,
				null,
				Type.EmptyTypes,
				null
			);

			if (restSiteReadyMethod != null)
			{
				var campfireTranspiler = typeof(CampfirePatch).GetMethod(
					nameof(CampfirePatch.Transpile_Ready),
					BindingFlags.Public | BindingFlags.Static);
				harmony.Patch(restSiteReadyMethod, transpiler: new HarmonyMethod(campfireTranspiler));
				Log.LogMessage(LogLevel.Debug, LogType.Generic, "Patched NRestSiteRoom._Ready (campfire fix)");
			}

			// Patch ALL ConstructDefault overloads with a PostFix that re-applies
			// our custom store after each call (covers profile-scoped re-initialisation).
			var saveManagerType = typeof(MegaCrit.Sts2.Core.Saves.SaveManager);
			var saveDirPostfix = typeof(SaveDirPatch).GetMethod(
				nameof(SaveDirPatch.Postfix_ConstructDefault),
				BindingFlags.Public | BindingFlags.Static);
			var constructDefaultMethods = saveManagerType
				.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
				.Where(m => m.Name == "ConstructDefault")
				.ToList();

			if (constructDefaultMethods.Count > 0)
			{
				foreach (var m in constructDefaultMethods)
				{
					harmony.Patch(m, postfix: new HarmonyMethod(saveDirPostfix));
					var sig = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
					Log.LogMessage(LogLevel.Debug, LogType.Generic,
						$"[SaveDirPatch] PostFix patched ConstructDefault({sig})");
				}
			}
			else
			{
				Log.LogMessage(LogLevel.Warn, LogType.Generic,
					"[SaveDirPatch] SaveManager.ConstructDefault not found — save-dir patch limited to ReplaceInstance");
			}

			// Patch InitProfileId — this is what sets the Steam-user-scoped path and overwrites
			// our store swap ("Profile-scoped data path initialized: user://steam/...").
			var initProfileIdMethod = saveManagerType.GetMethod(
				"InitProfileId",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
				null,
				[typeof(int?)],
				null
			);

			if (initProfileIdMethod != null)
			{
				var initProfilePostfix = typeof(SaveDirPatch).GetMethod(
					nameof(SaveDirPatch.Postfix_InitProfileId),
					BindingFlags.Public | BindingFlags.Static);
				harmony.Patch(initProfileIdMethod, postfix: new HarmonyMethod(initProfilePostfix));
				Log.LogMessage(LogLevel.Info, LogType.Generic,
					"[SaveDirPatch] Patched SaveManager.InitProfileId (save-dir support)");
			}
			else
			{
				Log.LogMessage(LogLevel.Warn, LogType.Generic,
					"[SaveDirPatch] SaveManager.InitProfileId not found — save-dir may not work after profile init");
			}

			// Patch SwitchProfileId — also updates path fields if the user changes profiles.
			var switchProfileIdMethod = saveManagerType.GetMethod(
				"SwitchProfileId",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
				null,
				[typeof(int)],
				null
			);

			if (switchProfileIdMethod != null)
			{
				var switchProfilePostfix = typeof(SaveDirPatch).GetMethod(
					nameof(SaveDirPatch.Postfix_SwitchProfileId),
					BindingFlags.Public | BindingFlags.Static);
				harmony.Patch(switchProfileIdMethod, postfix: new HarmonyMethod(switchProfilePostfix));
				Log.LogMessage(LogLevel.Debug, LogType.Generic,
					"[SaveDirPatch] Patched SaveManager.SwitchProfileId");
			}

			// Replace already-created singleton if --save-dir is present
			SaveDirPatch.ReplaceInstance();

			// Patch packet list serialization to support more than 7 items (3-bit limit)
			PacketSizePatch.Apply(harmony);

			// Build reflection cache for chest patch
			ChestPatch.RegisterReflectionCache();

			// Patch NTreasureRoomRelicCollection.InitializeRelics to support >4 players:
			// - extends the loop bound from _multiplayerHolders.Count to RunManager.NumPlayers
			// - bounds-clamps _multiplayerHolders[i] so extra players share the last holder
			var initRelicsMethod = ChestPatch.GetInitializeRelicsMethod();
			if (initRelicsMethod != null)
			{
				var chestPrefix = typeof(ChestPatch).GetMethod(
					nameof(ChestPatch.Prefix_InitializeRelics),
					BindingFlags.Public | BindingFlags.Static);
				var chestPostfix = typeof(ChestPatch).GetMethod(
					nameof(ChestPatch.Postfix_InitializeRelics),
					BindingFlags.Public | BindingFlags.Static);
				var chestTranspiler = typeof(ChestPatch).GetMethod(
					nameof(ChestPatch.Transpile_InitializeRelics),
					BindingFlags.Public | BindingFlags.Static);
				harmony.Patch(initRelicsMethod,
					prefix: new HarmonyMethod(chestPrefix),
					postfix: new HarmonyMethod(chestPostfix),
					transpiler: new HarmonyMethod(chestTranspiler));
				Log.LogMessage(LogLevel.Info, LogType.Generic,
					"[ChestPatch] Patched NTreasureRoomRelicCollection.InitializeRelics (chest fix)");
			}
			else
			{
				Log.LogMessage(LogLevel.Warn, LogType.Generic,
					"[ChestPatch] NTreasureRoomRelicCollection.InitializeRelics not found — chest fix skipped");
			}

			// Patch difficulty scaling to support player-count override
			DifficultyPatch.Apply(harmony);
		}
		catch (Exception e)
		{
			Log.LogMessage(LogLevel.Error, LogType.Generic, $"Failed to apply Harmony patches: {e.Message}");
		}
	}

	// Harmony patch methods - these intercept and modify the parameters before the actual method runs

	private static bool Patch_StartSteamHost(ref int maxClients)
	{
		Log.LogMessage(LogLevel.Info, LogType.Generic,
			$"[HostPatch] StartSteamHost called with maxClients={maxClients}, overriding to {MaxPlayersOverride}");
		maxClients = MaxPlayersOverride;
		return true;
	}

	private static bool Patch_StartENetHost(ushort port, ref int maxClients)
	{
		Log.LogMessage(LogLevel.Info, LogType.Generic,
			$"[HostPatch] StartENetHost called with port={port} maxClients={maxClients}, overriding to {MaxPlayersOverride}");
		maxClients = MaxPlayersOverride;
		return true;
	}

	// Shared by NCharacterSelectScreen and NCustomRunScreen's InitializeMultiplayerAsHost — both
	// have the same (INetGameService gameService, int maxPlayers) signature, matched by parameter
	// name regardless of declaring type (see DifficultyPatch's Prefix_ScaleHpForMultiplayer for the
	// same pattern).
	private static bool Patch_LobbyInitMaxPlayers(ref int maxPlayers)
	{
		Log.LogMessage(LogLevel.Info, LogType.Generic,
			$"[HostPatch] InitializeMultiplayerAsHost called with maxPlayers={maxPlayers}, overriding to {MaxPlayersOverride}");
		maxPlayers = MaxPlayersOverride;
		return true;
	}

}
