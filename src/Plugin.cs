using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

namespace FirestoneCardPopularity;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.firestoneapp.CardPopularity";
    public const string PluginName = "Firestone Card Popularity";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log = null!;
    internal static Plugin Instance = null!;
    internal static CardPopularityService Service = null!;
    internal static double PopularityThreshold { get; private set; }
    internal static int OverlayFontSize { get; private set; }
    internal static int OverlayVerticalOffset { get; private set; }
    internal static int OverlayHorizontalOffset { get; private set; }
    internal static string OverlayTextColor { get; private set; } = "#FFFFFF";
    internal static string OverlayPopularColor { get; private set; } = "#55FF55";
    internal static string OverlayUnpopularColor { get; private set; } = "#FF5555";
    internal static bool Enabled { get; private set; }
    internal static bool PositionMode { get; private set; }
    private static bool SettingsOpen;
    private static Rect SettingsRect = new Rect(40f, 40f, 420f, 430f);
    private static readonly ConcurrentQueue<Action> MainThreadActions = new();

    internal static void RunOnMainThread(Action action) => MainThreadActions.Enqueue(action);

    private void Update()
    {
        while (MainThreadActions.TryDequeue(out var action))
        {
            try { action(); } catch (Exception ex) { Log.LogWarning($"UI update failed: {ex.Message}"); }
        }

        // Read hotkeys from Update rather than OnGUI; Firestone/Overwolf can consume IMGUI key events.
        if (Input.GetKeyDown(KeyCode.F10) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
        {
            SettingsOpen = !SettingsOpen;
            Log.LogInfo($"Settings window: {(SettingsOpen ? "open" : "closed")}");
        }
        if (Input.GetKeyDown(KeyCode.F8))
        {
            SettingsOpen = !SettingsOpen;
            Log.LogInfo($"Settings window: {(SettingsOpen ? "open" : "closed")} (F8 fallback)");
        }
        if (Input.GetKeyDown(KeyCode.F9) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
        {
            PositionMode = !PositionMode;
            Log.LogInfo($"Overlay positioning mode: {(PositionMode ? "ON" : "OFF")}");
        }
        if (!PositionMode) return;
        var step = Input.GetKey(KeyCode.LeftShift) ? 10 : 1;
        if (Input.GetKey(KeyCode.LeftArrow)) OverlayHorizontalOffset -= step;
        if (Input.GetKey(KeyCode.RightArrow)) OverlayHorizontalOffset += step;
        if (Input.GetKey(KeyCode.UpArrow)) OverlayVerticalOffset -= step;
        if (Input.GetKey(KeyCode.DownArrow)) OverlayVerticalOffset += step;
        if (Input.GetKeyDown(KeyCode.S) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
        {
            Instance.Config["Overlay", "HorizontalOffset"].BoxedValue = OverlayHorizontalOffset;
            Instance.Config["Overlay", "VerticalOffset"].BoxedValue = OverlayVerticalOffset;
            Instance.Config.Save();
            Log.LogInfo($"Overlay position saved: X={OverlayHorizontalOffset}, Y={OverlayVerticalOffset}");
        }
    }

    private void OnGUI()
    {
        CollectionCardVisualPatch.DrawOverlay();
        var evt = Event.current;
        if (SettingsOpen && evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
        {
            SettingsOpen = false;
            evt.Use();
        }
        if (SettingsOpen) SettingsRect = GUI.Window(987654, SettingsRect, DrawSettingsWindow, "HSReplay Card Popularity");
    }

    private void DrawSettingsWindow(int id)
    {
        GUILayout.BeginVertical();
        GUILayout.BeginHorizontal();
        GUILayout.Label("Ctrl+F10 toggles this window.");
        if (GUILayout.Button("X", GUILayout.Width(28f))) SettingsOpen = false;
        GUILayout.EndHorizontal();
        var enabled = GUILayout.Toggle(Enabled, "Enable overlay");
        if (enabled != Enabled) SetEnabled(enabled);
        GUILayout.Label($"Font size: {OverlayFontSize}");
        OverlayFontSize = SliderInt(OverlayFontSize, 12, 64);
        GUILayout.Label($"Vertical offset: {OverlayVerticalOffset}");
        OverlayVerticalOffset = SliderInt(OverlayVerticalOffset, -500, 800);
        GUILayout.Label($"Horizontal offset: {OverlayHorizontalOffset}");
        OverlayHorizontalOffset = SliderInt(OverlayHorizontalOffset, -800, 800);
        GUILayout.Label("Text color (hex):");
        OverlayTextColor = GUILayout.TextField(OverlayTextColor);
        GUILayout.Label("Popular icon color (hex):");
        OverlayPopularColor = GUILayout.TextField(OverlayPopularColor);
        GUILayout.Label("Unpopular icon color (hex):");
        OverlayUnpopularColor = GUILayout.TextField(OverlayUnpopularColor);
        if (GUILayout.Button("Save settings")) SaveSettings();
        GUI.DragWindow(new Rect(0, 0, 10000, 24));
        GUILayout.EndVertical();
    }

    private static int SliderInt(int value, int min, int max) => Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max));

    private static void SetEnabled(bool value)
    {
        Enabled = value;
        Instance.Config["Firestone", "Enabled"].BoxedValue = value;
        Instance.Config.Save();
    }

    private static void SaveSettings()
    {
        Instance.Config["Overlay", "FontSize"].BoxedValue = OverlayFontSize;
        Instance.Config["Overlay", "VerticalOffset"].BoxedValue = OverlayVerticalOffset;
        Instance.Config["Overlay", "HorizontalOffset"].BoxedValue = OverlayHorizontalOffset;
        Instance.Config["Firestone", "TextColor"].BoxedValue = OverlayTextColor;
        Instance.Config["Firestone", "PopularColor"].BoxedValue = OverlayPopularColor;
        Instance.Config["Firestone", "UnpopularColor"].BoxedValue = OverlayUnpopularColor;
        Instance.Config.Save();
    }

    private void Awake()
    {
        Log = Logger;
        Instance = this;
        // Firestone discovers mods through this metadata and exposes settings
        // from sections named Firestone (or Firestone.*) in Settings > Mods.
        Config.Bind("General", "Name", PluginName);
        Config.Bind("General", "Guid", PluginGuid);
        Config.Bind("General", "Version", PluginVersion);
        Config.Bind("General", "DownloadLink", "https://github.com/holdeMp/firestone-hsreplay-card-popularity");
        var enabled = Config.Bind("Firestone", "Enabled", true, "Enable the HSReplay card popularity overlay.");
        var gameType = Config.Bind("HSReplay", "GameType", "RANKED_STANDARD", "HSReplay game type.");
        var rankRange = Config.Bind("HSReplay", "RankRange", "BRONZE_THROUGH_GOLD", "Gold includes Bronze through Gold on HSReplay.");
        var timeRange = Config.Bind("HSReplay", "TimeRange", "LAST_14_DAYS", "Statistics time range.");
        var refresh = Config.Bind("HSReplay", "RefreshMinutes", 30, "Cache duration in minutes.");
        var baseUrl = Config.Bind("HSReplay", "ApiBaseUrl", "https://hsreplay.net", "HSReplay endpoint root.");
        var normalFirst = Config.Bind("Cards", "PreferNormalCards", true, "Prefer normal cards over golden/signature variants.");
        var popularityThreshold = Config.Bind("Cards", "PopularityThreshold", 0.2, "Popularity percentage threshold for color/icon highlighting.");
        var overlayFontSize = Config.Bind("Overlay", "FontSize", 28, "IMGUI overlay font size.");
        var overlayVerticalOffset = Config.Bind("Overlay", "VerticalOffset", 260, "Pixels below card center; increase to move text downward.");
        var overlayHorizontalOffset = Config.Bind("Overlay", "HorizontalOffset", 0, "Horizontal pixel offset.");
        var overlayTextColor = Config.Bind("Firestone", "TextColor", "#FFFFFF", "Overlay text color as HTML hex, for example #FFFFFF.");
        var overlayPopularColor = Config.Bind("Firestone", "PopularColor", "#55FF55", "Popular icon color as HTML hex.");
        var overlayUnpopularColor = Config.Bind("Firestone", "UnpopularColor", "#FF5555", "Unpopular icon color as HTML hex.");

        Enabled = enabled.Value;
        enabled.SettingChanged += (_, __) =>
        {
            Enabled = enabled.Value;
            Log.LogInfo($"Card popularity overlay {(Enabled ? "enabled" : "disabled")} from configuration.");
        };
        PopularityThreshold = popularityThreshold.Value;
        OverlayFontSize = overlayFontSize.Value;
        OverlayVerticalOffset = overlayVerticalOffset.Value;
        OverlayHorizontalOffset = overlayHorizontalOffset.Value;
        OverlayTextColor = overlayTextColor.Value;
        OverlayPopularColor = overlayPopularColor.Value;
        OverlayUnpopularColor = overlayUnpopularColor.Value;
        overlayTextColor.SettingChanged += (_, __) => OverlayTextColor = overlayTextColor.Value;
        overlayPopularColor.SettingChanged += (_, __) => OverlayPopularColor = overlayPopularColor.Value;
        overlayUnpopularColor.SettingChanged += (_, __) => OverlayUnpopularColor = overlayUnpopularColor.Value;
        Service = new CardPopularityService(enabled, gameType, rankRange, timeRange, refresh, baseUrl, normalFirst, popularityThreshold);
        Log.LogInfo($"{PluginName} {PluginVersion} loaded");
        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll();
        Log.LogInfo("Collection card statistics integration enabled.");
    }
}

public sealed class CardPopularity
{
    public double? IncludedPercent { get; private set; }
    public double? Winrate { get; private set; }
    public int? Rank { get; private set; }
    public DateTimeOffset RetrievedAt { get; private set; }
    public CardPopularity(double? includedPercent, double? winrate, int? rank, DateTimeOffset retrievedAt) { IncludedPercent = includedPercent; Winrate = winrate; Rank = rank; RetrievedAt = retrievedAt; }
}

internal sealed class HsReplayResponse
{
    public HsReplaySeries series { get; set; } = new();
}
internal sealed class HsReplaySeries
{
    public Dictionary<string, List<HsReplayCard>> data { get; set; } = new();
}
internal sealed class HsReplayCard
{
    public int dbf_id { get; set; }
    public double included_popularity { get; set; }
    public double included_winrate { get; set; }
    public int DbfId { get { return dbf_id; } }
    public double IncludedPopularity { get { return included_popularity; } }
    public double IncludedWinrate { get { return included_winrate; } }
}

internal sealed class CardPopularityService
{
    private readonly ConfigEntry<bool> enabled;
    private readonly ConfigEntry<string> gameType, rankRange, timeRange, baseUrl;
    private readonly ConfigEntry<int> refreshMinutes;
    private readonly ConfigEntry<bool> normalFirst;
    private readonly ConfigEntry<double> popularityThreshold;
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, CardPopularity? Value)> cache = new();

    internal CardPopularityService(ConfigEntry<bool> enabled, ConfigEntry<string> gameType, ConfigEntry<string> rankRange,
        ConfigEntry<string> timeRange, ConfigEntry<int> refreshMinutes, ConfigEntry<string> baseUrl, ConfigEntry<bool> normalFirst, ConfigEntry<double> popularityThreshold)
    {
        this.enabled = enabled; this.gameType = gameType; this.rankRange = rankRange; this.timeRange = timeRange;
        this.refreshMinutes = refreshMinutes; this.baseUrl = baseUrl; this.normalFirst = normalFirst; this.popularityThreshold = popularityThreshold;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FirestoneCardPopularity/0.1");
    }

    internal async Task<CardPopularity?> GetAsync(string cardId)
    {
        if (!enabled.Value || string.IsNullOrWhiteSpace(cardId)) return null;
        var key = normalFirst.Value ? CardIds.Normalize(cardId) : cardId;
        if (cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(Math.Max(1, refreshMinutes.Value))) return hit.Value;

        // Kept isolated because HSReplay's web API is not a documented public contract.
        var url = $"{baseUrl.Value.TrimEnd('/')}/analytics/query/card_list_free/?" +
                  $"GameType={Uri.EscapeDataString(gameType.Value)}&TimeRange={Uri.EscapeDataString(timeRange.Value)}&LeagueRankRange={Uri.EscapeDataString(rankRange.Value)}";
        try
        {
            Plugin.Log.LogInfo($"Requesting HSReplay statistics: {url}");
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            Plugin.Log.LogInfo($"HSReplay response: {(int)response.StatusCode} {response.ReasonPhrase}");
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var payload = JsonConvert.DeserializeObject<HsReplayResponse>(json);
            if (payload?.series?.data == null || !payload.series.data.TryGetValue("ALL", out var rows)) return null;
            HsReplayCard? row = null;
            foreach (var candidate in rows)
            {
                // DbfId mapping is supplied by the Hearthstone card database adapter.
                if (CardDatabaseAdapter.Matches(candidate.DbfId, key)) { row = candidate; break; }
            }
            if (key == "CATA_497")
                Plugin.Log.LogInfo($"Ultraxion HSReplay match: {(row == null ? "NOT FOUND" : "FOUND")}");
            var value = row == null ? null : new CardPopularity(row.IncludedPopularity, row.IncludedWinrate, null, DateTimeOffset.UtcNow);
            cache[key] = (DateTimeOffset.UtcNow, value);
            return value;
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"HSReplay request failed for {key}: {ex.Message}"); return null; }
    }
}

internal static class CardDatabaseAdapter
{
    internal static bool Matches(int dbfId, string cardId)
    {
        var record = GameDbf.GetIndex().GetCardRecord(cardId);
        if (record == null)
        {
            Plugin.Log.LogWarning($"Card DBF lookup failed: {cardId}");
            return false;
        }
        if (cardId == "CATA_497")
            Plugin.Log.LogInfo($"Ultraxion DBF mapping: {cardId} -> {record.ID}, HSReplay row -> {dbfId}");
        return record.ID == dbfId;
    }
}

[HarmonyPatch(typeof(CollectionCardVisual), "SetActors")]
internal static class CollectionCardVisualPatch
{
    private static readonly Dictionary<CollectionCardVisual, string> OverlayValues = new();
    private static readonly GUIStyle OverlayStyle = new GUIStyle { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };

    private static Color ParseColor(string value, Color fallback)
    {
        return ColorUtility.TryParseHtmlString(value, out var color) ? color : fallback;
    }

    internal static void SetOverlay(CollectionCardVisual card, string value)
    {
        OverlayValues[card] = value;
        Plugin.Log.LogInfo($"Overlay value set: {card.CardId} = {value}");
    }

    internal static void DrawOverlay()
    {
        if (!Plugin.Enabled) return;
        // Tavern Brawl can reuse the same collection card visuals. Do not draw
        // Standard HSReplay statistics while that mode is active.
        try
        {
            var gameMgr = GameMgr.Get();
            // Draft is used by Arena and Tavern Brawl deck construction. The
            // collection-card patch also fires there, but Standard statistics
            // must never be shown in either mode.
            var sceneMode = SceneMgr.Get()?.GetMode();
            if (sceneMode == SceneMgr.Mode.DRAFT || sceneMode == SceneMgr.Mode.TAVERN_BRAWL)
                return;
            if (gameMgr != null && (gameMgr.IsTavernBrawl() || gameMgr.IsArena()))
                return;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Unable to determine game mode; suppressing overlay: {ex.Message}");
            return;
        }

        if (Time.frameCount % 120 == 0) Plugin.Log.LogInfo($"IMGUI overlay: entries={OverlayValues.Count}, screen={Screen.width}x{Screen.height}, camera={(Camera.main != null)}");
        if (OverlayValues.Count == 0) return;
        OverlayStyle.fontSize = Plugin.OverlayFontSize;
        OverlayStyle.normal.textColor = ParseColor(Plugin.OverlayTextColor, Color.white);
        foreach (var pair in OverlayValues.ToArray())
        {
            var card = pair.Key;
            if (card == null || !card.IsShown() || string.IsNullOrEmpty(card.CardId)) continue;
            var screen = Camera.main == null ? Vector3.zero : Camera.main.WorldToScreenPoint(card.transform.position);
            if (screen.z <= 0) { if (card.CardId == "CATA_497") Plugin.Log.LogInfo("Overlay skipped: card behind camera"); continue; }
            var rect = new Rect(screen.x - 140f + Plugin.OverlayHorizontalOffset, Screen.height - screen.y + Plugin.OverlayVerticalOffset, 280f, 50f);
            if (card.CardId == "CATA_497") Plugin.Log.LogInfo($"Overlay drawing Ultraxion: screen={screen}, rect={rect}, value={pair.Value}");
            GUI.Label(rect, pair.Value, OverlayStyle);
        }
    }

    private static readonly System.Collections.Generic.Dictionary<CollectionCardVisual, GameObject> Labels = new();

    internal static void Refresh(CollectionCardVisual __instance, Transform pageRoot)
    {
        Postfix(__instance);
    }

    private static void Postfix(CollectionCardVisual __instance)
    {
        if (!Plugin.Enabled) return;
        if (__instance == null || string.IsNullOrEmpty(__instance.CardId)) return;
        Plugin.Log.LogInfo($"Overlay card detected: {__instance.CardId}, shown={__instance.IsShown()}, active={__instance.gameObject.activeInHierarchy}, pos={__instance.transform.position}");
        SetOverlay(__instance, "loading...");
        _ = UpdateAsync(__instance);
    }

    private static readonly Dictionary<CollectionCardVisual, UberText> FallbackLabels = new();

    private static UberText GetOrCreateFallback(CollectionCardVisual card)
    {
        if (FallbackLabels.TryGetValue(card, out var existing) && existing != null) return existing;
        UberText text;
        var source = card.m_count != null ? card.m_count.m_normalCountText : null;
        if (source != null)
        {
            var clone = UnityEngine.Object.Instantiate(source.gameObject, card.transform);
            clone.name = "HSReplayCraftablePopularity";
            clone.SetActive(true);
            clone.transform.localPosition = source.transform.localPosition + new Vector3(0f, -0.18f, -0.02f);
            clone.transform.localRotation = source.transform.localRotation;
            clone.transform.localScale = source.transform.localScale;
            foreach (var group in clone.GetComponentsInChildren<CanvasGroup>(true)) group.alpha = 1f;
            text = clone.GetComponent<UberText>();
        }
        else
        {
            var go = new GameObject("HSReplayCraftablePopularity");
            go.transform.SetParent(card.transform, false);
            text = go.AddComponent<UberText>();
            text.transform.localPosition = new Vector3(0f, -1.1f, -0.25f);
            text.transform.localScale = Vector3.one;
        }
        Style(text);
        FallbackLabels[card] = text;
        return text;
    }

    private static void Style(UberText text)
    {
        text.FontSize = 120;
        text.BoldSize = 6f;
        text.OutlineSize = 3f;
        text.OutlineColor = new Color(0f, 0f, 0f, 1f);
        text.OutlineAlpha = 1f;
        text.TextColor = new Color(1f, 1f, 1f, 1f);
        text.TextAlpha = 1f;
    }

    private static GameObject GetOrCreate(CollectionCardVisual card)
    {
        if (Labels.TryGetValue(card, out var existing) && existing != null) return existing;
        var go = new GameObject("HSReplayCardPopularity", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(card.transform, false);
        var rect = go.GetComponent<RectTransform>();
        rect.localPosition = new Vector3(0f, -1.35f, -0.2f);
        rect.localScale = Vector3.one;
        rect.sizeDelta = new Vector2(500f, 80f);
        var text = go.GetComponent<Text>();
        text.text = "HSReplay: loading...";
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.fontSize = 24;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = 12;
        text.resizeTextMaxSize = 24;
        Labels[card] = go;
        return go;
    }

    private static async Task UpdateAsync(CollectionCardVisual card)
    {
        if (card == null) return;
        var cardId = card.CardId;
        Plugin.RunOnMainThread(() => SetOverlay(card, "loading..."));
        var stats = await Plugin.Service.GetAsync(cardId);
        Plugin.RunOnMainThread(() =>
        {
            if (card == null || card.CardId != cardId) return;
            if (stats == null) { SetOverlay(card, "n/a"); return; }
            var popular = stats.IncludedPercent >= Plugin.PopularityThreshold;
            var icon = popular ? $"<color={Plugin.OverlayPopularColor}>▲</color>" : $"<color={Plugin.OverlayUnpopularColor}>▼</color>";
            SetOverlay(card, $"{icon} {stats.IncludedPercent:0.00}%  WR {stats.Winrate:0.00}%");
        });
    }

    private static IEnumerator Reapply(CollectionCardVisual card, string value)
    {
        for (var i = 0; i < 4; i++)
        {
            yield return null;
            Apply(card, value);
        }
    }

    private static void Apply(CollectionCardVisual card, string value)
    {
        if (card == null || card.CardId != "CATA_497") return;
        Plugin.Log.LogInfo($"Ultraxion apply: count={(card.m_count != null)}, text={(card.m_count != null && card.m_count.m_normalCountText != null)}, cardActive={card.gameObject.activeInHierarchy}");
        if (card.m_count == null || card.m_count.m_normalCountText == null) return;
        card.m_count.Show();
        var text = card.m_count.m_normalCountText;
        Style(text);
        text.Text = value;
    }
}

internal static class CardIds
{
    internal static string Normalize(string id)
    {
        if (id.EndsWith("_GOLDEN", StringComparison.OrdinalIgnoreCase)) return id.Substring(0, id.Length - 7);
        if (id.EndsWith("_SIGNATURE", StringComparison.OrdinalIgnoreCase)) return id.Substring(0, id.Length - 10);
        return id;
    }
}
