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

	// In a multiplayer run the host's value is authoritative for every peer: monster HP and
	// block are derived locally and then checksummed, so a per-client value desyncs the run.
	// Singleplayer keeps the local setting, including the challenge-mode toggle.
	public static int GetEffectivePlayerCount(int actualCount)
		=> DifficultySync.Resolve(actualCount, DifficultyOverrideEnabled,
			DifficultyPlayersOverride, DifficultyScaleSingleplayerEnabled);

	public static void ModLoaded()
	{
		LoadConfig();
		ApplyHarmonyPatches();
		SettingsMenuIntegration.InitializeSettingsMenuUI(harmony);

		// Never report success without checking. A patch that quietly stopped
		// matching gets named here instead of being buried in one Warn line.
		int broken = PatchRegistry.RequiredFailures;
		if (broken > 0)
		{
			Log.LogMessage(LogLevel.Error, LogType.Generic,
				$"Sts2Unlimited loaded DEGRADED — {broken} required patch(es) are not active. " +
				$"maxPlayers set to {MaxPlayersOverride}. {PatchRegistry.Summary()}");
		}
		else
		{
			Log.LogMessage(LogLevel.Info, LogType.Generic,
				$"Sts2Unlimited loaded successfully! maxPlayers set to {MaxPlayersOverride} " +
				$"({PatchRegistry.Summary()})");
		}
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
				PatchRegistry.Record("NetHostGameService.StartSteamHost", true);
			}
			else
			{
				PatchRegistry.Record("NetHostGameService.StartSteamHost", false,
					detail: $"StartSteamHost(int) not found. Members: {netHostMethodList}");
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
				PatchRegistry.Record("NetHostGameService.StartENetHost", true);
			}
			else
			{
				PatchRegistry.Record("NetHostGameService.StartENetHost", false,
					detail: $"StartENetHost(UInt16, int) not found. Members: {netHostMethodList}");
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
			// All four mode screens share this signature and pass maxPlayers straight into
			// StartRunLobby, so one prefix covers them all. Patching only some of them caps
			// the rest at four: StartENetHost accepts the fifth peer's handshake and
			// StartRunLobby then kicks it with NetError.LobbyFull. NDailyRunScreen is
			// deliberately absent — its overload takes no maxPlayers at all, so daily runs
			// are capped elsewhere.
			PatchLobbyInit(typeof(MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen));
			PatchLobbyInit(typeof(MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen));
			PatchLobbyInit(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Blitz.NBlitzScreen));
			PatchLobbyInit(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Dread.NDreadScreen));

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
				PatchRegistry.Record("NRestSiteRoom._Ready (campfire)", CampfirePatch.Applied,
					detail: "transpiler installed but found no matching IL site");
			}
			else
			{
				PatchRegistry.Record("NRestSiteRoom._Ready (campfire)", false, detail: "_Ready() not found");
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
				PatchRegistry.Record("SaveManager.ConstructDefault", true, required: false);
			}
			else
			{
				PatchRegistry.Record("SaveManager.ConstructDefault", false, required: false,
					detail: "not found — save-dir patch limited to ReplaceInstance");
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
				PatchRegistry.Record("SaveManager.InitProfileId", true);
			}
			else
			{
				PatchRegistry.Record("SaveManager.InitProfileId", false,
					detail: "not found — save-dir may not work after profile init");
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
				PatchRegistry.Record("SaveManager.SwitchProfileId", true, required: false);
			}
			else
			{
				PatchRegistry.Record("SaveManager.SwitchProfileId", false, required: false,
					detail: "not found");
			}

			// Replace already-created singleton if --save-dir is present
			SaveDirPatch.ReplaceInstance();

			// Patch packet list serialization to support more than 7 items (3-bit limit)
			PacketSizePatch.Apply(harmony);

			// Guard the same headers at PacketWriter.WriteList / PacketReader.ReadList. Another
			// mod (STS2-RitsuLib) patches NetMessageBus.SerializeMessage<LobbyBeginRunMessage>
			// before we load, which makes the JIT inline the unpatched Serialize into its
			// replacement — a copy the transpiler above can never reach. Must run after
			// PacketSizePatch.Apply, which is what populates RewrittenListElementTypes.
			ListHeaderWidthPatch.Apply(harmony);

			// Build reflection cache for chest patch
			// Keeps the shared treasure chest usable when there are more relics than
			// the screen ships holders for — which is any chest at 7+ players, or 5+ in
			// Blitz. ChestPatch explains the mechanism and why it takes two halves.
			ApplyChestPatch();

			// Patch difficulty scaling to support player-count override
			DifficultyPatch.PlayerCountResolver = GetEffectivePlayerCount;
			DifficultyPatch.Apply(harmony);

			// DifficultySync is compiled into the headless harness, which does not compile this
			// file — so it reads the local settings through providers rather than directly.
			DifficultySync.LocalOverrideEnabled = () => DifficultyOverrideEnabled;
			DifficultySync.LocalPlayersOverride = () => DifficultyPlayersOverride;
			DifficultySync.Apply(harmony);
		}
		catch (Exception e)
		{
			Log.LogMessage(LogLevel.Error, LogType.Generic, $"Failed to apply Harmony patches: {e.Message}");
		}
	}

	// Harmony patch methods - these intercept and modify the parameters before the actual method runs

	/// <summary>
	/// Overrides one host screen's hardcoded 4-player lobby cap.
	/// </summary>
	private static void PatchLobbyInit(Type screenType)
	{
		string target = $"{screenType.Name}.InitializeMultiplayerAsHost";

		var initMethod = screenType.GetMethod(
			"InitializeMultiplayerAsHost",
			BindingFlags.Public | BindingFlags.Instance,
			null,
			[typeof(INetGameService), typeof(int)],
			null
		);

		if (initMethod == null)
		{
			PatchRegistry.Record(target, false, detail: "(INetGameService, int) overload not found");
			return;
		}

		var prefix = typeof(Sts2Unlimited).GetMethod(
			nameof(Patch_LobbyInitMaxPlayers), BindingFlags.NonPublic | BindingFlags.Static);
		harmony.Patch(initMethod, prefix: new HarmonyMethod(prefix));
		PatchRegistry.Record(target, true);
	}

	/// <summary>
	/// Installs the shared-chest fix: a transpiler to pick a layout that exists
	/// and a postfix to fill it. Both are required — see ChestPatch for why
	/// clamping the layout alone still hangs the run.
	/// </summary>
	private static void ApplyChestPatch()
	{
		const string target = "NTreasureRoomRelicCollection.InitializeRelics (chest)";

		if (!ChestPatch.TryInitialize())
		{
			PatchRegistry.Record(target, false, detail: "required game members missing");
			return;
		}

		var initRelicsMethod = ChestPatch.GetInitializeRelicsMethod();
		if (initRelicsMethod == null)
		{
			PatchRegistry.Record(target, false, detail: "InitializeRelics() not found");
			return;
		}

		var chestPostfix = typeof(ChestPatch).GetMethod(
			nameof(ChestPatch.Postfix_InitializeRelics),
			BindingFlags.Public | BindingFlags.Static);
		var chestTranspiler = typeof(ChestPatch).GetMethod(
			nameof(ChestPatch.Transpile_InitializeRelics),
			BindingFlags.Public | BindingFlags.Static);

		harmony.Patch(initRelicsMethod,
			postfix: new HarmonyMethod(chestPostfix),
			transpiler: new HarmonyMethod(chestTranspiler));

		// Harmony runs the transpiler during Patch(), so by now it has already
		// reported whether it found the site it needed.
		PatchRegistry.Record(target, ChestPatch.TranspilerApplied,
			detail: "transpiler found no layout-selection site to clamp");
	}

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
