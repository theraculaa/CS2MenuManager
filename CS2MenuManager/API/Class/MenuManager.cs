using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2MenuManager.API.Interface;
using CS2MenuManager.API.Menu;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CS2MenuManager.API.Class;

/// <summary>
/// Manages the active menus for players.
/// </summary>
public static class MenuManager
{
    private static readonly Dictionary<ulong, (IMenuInstance Instance, Timer? Timer)> ActiveMenus = [];

    // osnet: only the latest OpenMenu call per player may create an instance on the next frame.
    private static readonly Dictionary<ulong, (long Token, int Slot)> PendingOpens = [];
    private static long _openSequence;

    // osnet: live WASD tick listeners per slot, to prove that menus do not pile up on one player.
    private static readonly Dictionary<int, int> LiveTicks = [];

    /// <summary>
    /// A static readonly dictionary that maps menu type names to their corresponding <see cref="Type"/> objects.
    /// </summary>
    public static readonly Dictionary<string, Type> MenuTypesList = new()
    {
        { "ConsoleMenu", typeof(ConsoleMenu) },
        { "ChatMenu", typeof(ChatMenu) },
        { "WasdMenu", typeof(WasdMenu) },
        { "CenterHtmlMenu", typeof(CenterHtmlMenu) }
    };

    /// <summary>
    /// osnet: the dictionary key for a player. Bots all have SteamID 0, so they are keyed by slot instead.
    /// Only read while the player is known to be valid; cleanup uses the key stored on the instance.
    /// </summary>
    private static bool TryKey(CCSPlayerController? player, out ulong key)
    {
        key = 0;
        try
        {
            if (player is null || !player.IsValid)
                return false;

            ulong steamId = player.SteamID;
            key = steamId != 0 ? steamId : (1UL << 63) | (uint)player.Slot;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the active menu for the specified player.
    /// </summary>
    /// <param name="player">The player controller.</param>
    /// <returns>The active menu instance, or null if no menu is active.</returns>
    public static IMenuInstance? GetActiveMenu(CCSPlayerController player)
    {
        if (!TryKey(player, out ulong key) || !ActiveMenus.TryGetValue(key, out (IMenuInstance Instance, Timer? Timer) value))
            return null;

        // osnet: a menu that belonged to an earlier connection of this player is never handed back.
        if (value.Instance is BaseMenuInstance baseInstance && (baseInstance.IsClosed || !baseInstance.OwnerValid() || baseInstance.OwnerUserId != SafeUserId(player)))
        {
            RemoveEntry(key, value.Instance);
            baseInstance.ForceClose("stale menu found for a new connection");
            return null;
        }

        return value.Instance;
    }

    private static int? SafeUserId(CCSPlayerController player)
    {
        try { return player.UserId; }
        catch { return null; }
    }

    private static void RemoveEntry(ulong key, IMenuInstance instance)
    {
        if (!ActiveMenus.TryGetValue(key, out (IMenuInstance Instance, Timer? Timer) value) || !ReferenceEquals(value.Instance, instance))
            return;

        try { value.Timer?.Kill(); }
        catch { /* timer already gone */ }

        ActiveMenus.Remove(key);
    }

    /// <summary>
    /// Closes the active menu for the specified player.
    /// </summary>
    /// <param name="player">The player controller.</param>
    public static void CloseActiveMenu(CCSPlayerController player)
    {
        if (!TryKey(player, out ulong key))
            return;

        PendingOpens.Remove(key);
        CloseByKey(key);
    }

    private static void CloseByKey(ulong key)
    {
        if (!ActiveMenus.TryGetValue(key, out (IMenuInstance Instance, Timer? Timer) value))
            return;

        // osnet: drop the entry and its timer first, so cleanup never depends on Close() succeeding.
        RemoveEntry(key, value.Instance);

        try
        {
            value.Instance.Close(true);
        }
        catch (Exception ex)
        {
            if (value.Instance is BaseMenuInstance baseInstance)
                baseInstance.ForceClose($"Close threw {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// osnet: closes every menu and cancels every pending open that belongs to a slot,
    /// without reading the player's SteamID. Used when a player disconnects.
    /// </summary>
    /// <param name="slot">The player slot.</param>
    public static void CloseMenusForSlot(int slot)
    {
        foreach (ulong key in PendingOpens.Where(p => p.Value.Slot == slot).Select(p => p.Key).ToList())
            PendingOpens.Remove(key);

        List<(ulong Key, IMenuInstance Instance)> owned = ActiveMenus
            .Where(kv => kv.Value.Instance is BaseMenuInstance b && b.OwnerSlot == slot)
            .Select(kv => (kv.Key, kv.Value.Instance))
            .ToList();

        foreach ((ulong key, IMenuInstance instance) in owned)
        {
            RemoveEntry(key, instance);
            if (instance is BaseMenuInstance baseInstance)
                baseInstance.ForceClose("slot cleanup on disconnect");
        }
    }

    internal static void DisposeActiveMenu(BaseMenuInstance instance)
    {
        RemoveEntry(instance.MenuKey, instance);
    }

    internal static void TickAdded(BaseMenuInstance instance)
    {
        int slot = instance.OwnerSlot;
        LiveTicks[slot] = LiveTicks.GetValueOrDefault(slot) + 1;
        if (LiveTicks[slot] > 1)
            Diag.Warn(instance.Menu.Plugin, $"slot={slot} has {LiveTicks[slot]} live WASD tick listeners (expected 1)");
    }

    internal static void TickRemoved(BaseMenuInstance instance)
    {
        int slot = instance.OwnerSlot;
        int left = LiveTicks.GetValueOrDefault(slot) - 1;
        if (left <= 0) LiveTicks.Remove(slot);
        else LiveTicks[slot] = left;
    }

    /// <summary>
    /// osnet: number of live WASD tick listeners per slot (diagnostics).
    /// </summary>
    public static IReadOnlyDictionary<int, int> LiveTickCounts => LiveTicks;

    /// <summary>
    /// osnet: number of tracked active menus (diagnostics).
    /// </summary>
    public static int ActiveMenuCount => ActiveMenus.Count;

    /// <summary>
    /// Opens a menu for the specified player.
    /// </summary>
    /// <typeparam name="TMenu">The type of the menu.</typeparam>
    /// <param name="player">The player controller.</param>
    /// <param name="menu">The menu to open.</param>
    /// <param name="firstItem">The index of the first item to display.</param>
    /// <param name="createInstance">The function to create a menu instance.</param>
    public static void OpenMenu<TMenu>(CCSPlayerController player, TMenu menu, int? firstItem, Func<CCSPlayerController, TMenu, IMenuInstance> createInstance)
        where TMenu : IMenu
    {
        if (menu.ItemOptions.Count == 0)
        {
            player.PrintToChat(player.Localizer("OptionListEmpty"));
            return;
        }

        if (!TryKey(player, out ulong key))
            return;

        int slot = player.Slot;
        int? userId = SafeUserId(player);

        CloseActiveMenu(player);

        long token = ++_openSequence;
        if (PendingOpens.ContainsKey(key))
            Diag.Info(menu.Plugin, $"open superseded before creation: slot={slot}");
        PendingOpens[key] = (token, slot);

        Server.NextFrame(() =>
        {
            // osnet: a newer open (or a close/disconnect) replaced this one.
            if (!PendingOpens.TryGetValue(key, out (long Token, int Slot) pending) || pending.Token != token)
                return;
            PendingOpens.Remove(key);

            // osnet: the player may have left during the frame (for example kicked while the command ran).
            bool sameConnection;
            try
            {
                sameConnection = player.IsValid
                    && player.Connected == PlayerConnectedState.Connected
                    && player.Slot == slot
                    && player.UserId == userId;
            }
            catch
            {
                sameConnection = false;
            }

            if (!sameConnection)
            {
                Diag.Info(menu.Plugin, $"open dropped, player left before the menu was created: slot={slot} userid={userId}");
                return;
            }

            // osnet: never let two instances live under one key.
            CloseByKey(key);

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            IMenuInstance instance = createInstance.Invoke(player, menu);

            if (instance is BaseMenuInstance baseMenuInstance)
            {
                baseMenuInstance.MenuKey = key;
                baseMenuInstance.RegisterOnKeyPress();
                baseMenuInstance.RegisterPlayerDisconnectEvent();

                if (firstItem.HasValue)
                {
                    int item = Math.Clamp(firstItem.Value, 0, menu.ItemOptions.Count - 1);

                    while (baseMenuInstance.CurrentChoiceIndex != item)
                    {
                        baseMenuInstance.CurrentChoiceIndex++;

                        if (baseMenuInstance.CurrentChoiceIndex >= baseMenuInstance.CurrentOffset + baseMenuInstance.NumPerPage)
                            baseMenuInstance.NextPage();
                    }

                    baseMenuInstance.CurrentChoiceIndex -= baseMenuInstance.CurrentOffset;
                }
            }

            // osnet: the timer closes this exact instance, never a newer menu opened later.
            Timer? timer = menu.MenuTime > 0 ?
                menu.Plugin.AddTimer(menu.MenuTime, () =>
                {
                    RemoveEntry(key, instance);
                    try { instance.Close(true); }
                    catch (Exception ex)
                    {
                        if (instance is BaseMenuInstance b) b.ForceClose($"timer close threw {ex.GetType().Name}");
                    }
                }) :
                null;

            ActiveMenus[key] = (instance, timer);

            instance.Display();

            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // osnet: open/close lines let the server log line up menus with the engine's netchan queue warnings.
            Diag.Info(menu.Plugin, $"menu opened: slot={slot} type={instance.GetType().Name} items={menu.ItemOptions.Count} {ms:F1}ms");
        });
    }

    /// <summary>
    /// Handles key press events for the active menu of the specified player.
    /// </summary>
    /// <param name="player">The player controller.</param>
    /// <param name="key">The key that was pressed.</param>
    public static void OnKeyPress(CCSPlayerController player, int key)
    {
        GetActiveMenu(player)?.OnKeyPress(player, key);
    }

    /// <summary>
    /// Creates a menu instance of a specified type
    /// </summary>
    /// <typeparam name="T">Type of menu to create (must implement BaseMenu)</typeparam>
    /// <param name="title">The title of the menu.</param>
    /// <param name="plugin">The plugin associated with the menu.</param>
    /// <returns>New menu instance of a requested type</returns>
    public static T CreateMenu<T>(string title, BasePlugin plugin) where T : BaseMenu
    {
        return (T)MenuByType(typeof(T), title, plugin);
    }

    /// <summary>
    /// Creates a menu instance based on Type parameter
    /// </summary>
    /// <param name="menuType">Type of menu to create</param>
    /// <param name="title">The title of the menu.</param>
    /// <param name="plugin">The plugin associated with the menu.</param>
    /// <returns>New menu instance of a requested type</returns>
    public static BaseMenu MenuByType(Type menuType, string title, BasePlugin plugin)
    {
        return menuType switch
        {
            { } t when t == typeof(PlayerMenu) => new PlayerMenu(title, plugin),
            { } t when t == typeof(ChatMenu) => new ChatMenu(title, plugin),
            { } t when t == typeof(ConsoleMenu) => new ConsoleMenu(title, plugin),
            { } t when t == typeof(CenterHtmlMenu) => new CenterHtmlMenu(title, plugin),
            { } t when t == typeof(WasdMenu) => new WasdMenu(title, plugin),
            _ => throw new ArgumentException($"Unsupported menu type: {menuType.FullName}", nameof(menuType))
        };
    }

    /// <summary>
    /// Creates a menu instance based on (string)Type parameter.
    /// </summary>
    /// <param name="menuType">Type of menu to create</param>
    /// <param name="title">The title of the menu.</param>
    /// <param name="plugin">The plugin associated with the menu.</param>
    /// <returns>New menu instance of a requested type</returns>
    public static BaseMenu MenuByType(string menuType, string title, BasePlugin plugin)
    {
        return menuType switch
        {
            "PlayerMenu" => new PlayerMenu(title, plugin),
            "ChatMenu" => new ChatMenu(title, plugin),
            "ConsoleMenu" => new ConsoleMenu(title, plugin),
            "CenterHtmlMenu" => new CenterHtmlMenu(title, plugin),
            "WasdMenu" => new WasdMenu(title, plugin),
            _ => throw new ArgumentException($"Unsupported menu type", nameof(menuType))
        };
    }

    /// <summary>
    /// Reloads config variables
    /// </summary>
    public static void ReloadConfig()
    {
        ConfigManager.LoadConfig();
    }
}