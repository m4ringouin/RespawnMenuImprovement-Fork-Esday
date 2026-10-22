using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI.HudViewers;
using Sandbox.Game.Localization;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.Screens.Helpers.RadialMenuActions;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using SpaceEngineers.Game.GUI;
using SpaceEngineers.Game.World;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRageMath;

namespace RespawnMenuImprovements;

public static class Patches
{
    private static MyGuiControlSearchBox searchBox = null;
    private static MyGuiControlTable respawnsTable = null;
    private static List<MyGuiControlTable.Row> allRows = null;
    private static readonly StringBuilder respawnPointTooltip = new StringBuilder();
    private static long m_restrictedRespawn = -1L;
    private static SortType sortStatus = SortType.None;
    private static MyGuiControlCombobox playersFilterDropdown = null;
    private static MyHudControlChat chatHistoryControl = null;
    private static Vector2 originalChatSize = new Vector2(0.339f, 0.28f);

    // Special dropdown key for the "Me + friends" entry (0 = "No Filter", real owners use their identity id)
    private const long FriendsFilterKey = -1L;
    private const string FriendsFilterLabel = "Me + friends";

    private static List<string> friendNames = null;
    private static SavedState savedState = null;
    private static bool autoSelectDone = false;
    private static bool autoSelectPending = false;
    private static int autoSelectFramesLeft = 0;

    private class SavedState
    {
        public long LastSpawnId = 0L;
        public long LastFilterKey = FriendsFilterKey;
    }

    private static string StorageDir => Path.Combine(MyFileSystem.UserDataPath, "Storage");
    private static string ConfigPath => Path.Combine(StorageDir, "RespawnMenuImprovements.Config.xml");
    private static string StatePath => Path.Combine(StorageDir, "RespawnMenuImprovements.State.xml");

    // Display names of the players whose spawns are shown in "Me + friends".
    // Edit the config file to change them. It is created with a default on first run.
    private static List<string> GetFriendNames()
    {
        if (friendNames != null)
            return friendNames;

        friendNames = new List<string>();
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Directory.CreateDirectory(StorageDir);
                new XDocument(
                    new XElement("Config",
                        new XElement("Friends",
                            new XElement("Name", "WarDave")))).Save(ConfigPath);
            }

            XDocument doc = XDocument.Load(ConfigPath);
            foreach (XElement e in doc.Descendants("Name"))
            {
                string name = e.Value?.Trim();
                if (!string.IsNullOrEmpty(name))
                    friendNames.Add(name);
            }
        }
        catch { }

        return friendNames;
    }

    private static SavedState GetSavedState()
    {
        if (savedState != null)
            return savedState;

        savedState = new SavedState();
        try
        {
            if (File.Exists(StatePath))
            {
                XElement root = XDocument.Load(StatePath).Root;
                if (long.TryParse(root?.Element("LastSpawnId")?.Value, out long spawnId))
                    savedState.LastSpawnId = spawnId;
                if (long.TryParse(root?.Element("LastFilterKey")?.Value, out long filterKey))
                    savedState.LastFilterKey = filterKey;
            }
        }
        catch { }

        return savedState;
    }

    private static void SaveState()
    {
        try
        {
            SavedState state = GetSavedState();
            Directory.CreateDirectory(StorageDir);
            new XDocument(
                new XElement("State",
                    new XElement("LastSpawnId", state.LastSpawnId),
                    new XElement("LastFilterKey", state.LastFilterKey))).Save(StatePath);
        }
        catch { }
    }

    // Owner ids shown by "Me + friends": the local player + owners whose display name is in the config.
    private static HashSet<long> GetFriendOwnerIds()
    {
        HashSet<long> ids = new HashSet<long>();
        ids.Add(MySession.Static.LocalPlayerId);

        if (allRows == null)
            return ids;

        List<string> names = GetFriendNames();
        for (int i = 0; i < allRows.Count; i++)
        {
            if (allRows[i].UserData is MySpaceRespawnComponent.MyRespawnPointInfo data)
            {
                string ownerName = GetOwnerDisplayName(data.OwnerId).ToString();
                if (names.Any(n => string.Equals(n, ownerName, StringComparison.OrdinalIgnoreCase)))
                    ids.Add(data.OwnerId);
            }
        }
        return ids;
    }

    private static void Log(string message)
    {
        try { VRage.Utils.MyLog.Default?.WriteLine("[RespawnMenuImprovements] " + message); }
        catch { }
    }

    // Called every frame from Main.Update(). Selects the last used spawn once the table is filled,
    // only if that spawn is in the currently filtered list.
    public static void Update()
    {
        if (!autoSelectPending || respawnsTable == null)
            return;

        try
        {
            if (autoSelectFramesLeft-- <= 0)
            {
                autoSelectPending = false;
                autoSelectDone = true;
                Log("Auto-select: last spawn not found in the filtered list, giving up.");
                return;
            }

            long lastSpawnId = GetSavedState().LastSpawnId;
            if (lastSpawnId == 0L)
            {
                autoSelectPending = false;
                autoSelectDone = true;
                Log("Auto-select: no saved spawn.");
                return;
            }

            for (int i = 0; i < respawnsTable.RowsCount; i++)
            {
                MyGuiControlTable.Row row = respawnsTable.Rows[i];
                if (row.UserData is MySpaceRespawnComponent.MyRespawnPointInfo info && info.MedicalRoomId == lastSpawnId)
                {
                    respawnsTable.SelectedRow = row;
                    autoSelectPending = false;
                    autoSelectDone = true;
                    Log("Auto-select: selected spawn " + lastSpawnId);
                    return;
                }
            }
        }
        catch (Exception e)
        {
            autoSelectPending = false;
            autoSelectDone = true;
            Log("Auto-select error: " + e);
        }
    }

    static Vector2 chatControlPos
    {
        get
        {
            Vector2 hudPos1 = new Vector2(0.014f, 0.81f);
            Vector2 hudPos2 = MyGuiScreenHudBase.ConvertHudToNormalizedGuiPosition(ref hudPos1);
            return hudPos2 + new Vector2(1f / 500f, -0.07f);
        }
    }

    private enum SortType
    {
        None = 0,
        NameAscending = 1,
        NameDescending = 2,
        OwnerAscending = 4,
        OwnerDescending = 8,
    }

    [HarmonyPatch(typeof(MyGuiScreenMedicals), nameof(MyGuiScreenMedicals.RecreateControls))]
    public static class Patch_MyGuiScreenMedicals_RecreateControls
    {
        public static void Postfix(MyGuiScreenMedicals __instance, MyGuiControlButton ___m_MotdButton)
        {
            float buttonPosY = __instance.Size.Value.Y / 2f - 0.153f;

            __instance.Controls.Remove(___m_MotdButton);
            ___m_MotdButton.Size = new Vector2((0.36f / 3f * 2f) - 0.004f, 0.035f);
            ___m_MotdButton.Position = new Vector2(0.002f - ((0.36f - ___m_MotdButton.Size.X) / 2f), buttonPosY);
            __instance.Controls.Add(___m_MotdButton);

            Vector2 chatBtnSize = new Vector2(0.36f / 3f, 0.035f);
            Vector2 chatBtnPos = new Vector2(0.0015f + ((0.36f - chatBtnSize.X) / 2f), buttonPosY);

            MyGuiControlButton chatBtn = new MyGuiControlButton(
                chatBtnPos,
                MyGuiControlButtonStyleEnum.Rectangular,
                chatBtnSize,
                null,
                VRage.Utils.MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                null,
                new StringBuilder(MyTexts.GetString(MySpaceTexts.ControlMenuItemLabel_Chat)),
                0.8f,
                VRage.Utils.MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                MyGuiControlHighlightType.WHEN_CURSOR_OVER,
                (btn) => ShowChatInput());
            __instance.Controls.Add(chatBtn);

            chatHistoryControl = MyHud.Chat.ChatControl ?? new MyHudControlChat(MyHud.Chat, textScale: 0.7f);
            chatHistoryControl.Position = chatControlPos - __instance.GetPositionAbsolute();
            originalChatSize = chatHistoryControl.Size;
            chatHistoryControl.Size = new Vector2(0.339f, 0.75f);
            chatHistoryControl.MouseOverChanged += Chat_MouseOverChanged;
            //ultra jank way to ensure chat is visible
            chatHistoryControl.Visibility = MyHudControlChat.MyChatVisibilityEnum.AlwaysVisible;
            MyHud.Chat.ChatOpened();
            MyHud.Chat.Update();
            MyHud.Chat.ChatClosed();
            __instance.Controls.Add(chatHistoryControl);
        }

        static void ShowChatInput()
        {
            new MyActionChat().OpenChatScreen();
            MyHud.Chat.Update();
        }
    }

    [HarmonyPatch(typeof(MyGuiScreenMedicals), nameof(MyGuiScreenMedicals.RecreateControlsRespawn))]
    public static class Patch_MyGuiScreenMedicals_RecreateControlsRespawn
    {
        public static void Postfix(MyGuiScreenMedicals __instance, MyGuiControlTable ___m_respawnsTable)
        {
            searchBox = new MyGuiControlSearchBox(new Vector2(-0.0745f, 0.244f), new Vector2(0.21f, 0f));
            searchBox.OnTextChanged += OnSearchBoxTextChanged;

            __instance.Controls.Add(searchBox);

            ___m_respawnsTable.ItemMouseOver += OnRespawnTableItemMouseOver;
            ___m_respawnsTable.ItemFocus += OnRespawnTableItemMouseOver;
            ___m_respawnsTable.ColumnClicked += OnRespawnTableColumnClicked;
        }
    }

    [HarmonyPatch]
    public static class Patch_MyGuiScreenMedicals_RefreshMedicalRooms_AddMedicalRespawnPoints
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.FirstMethod(typeof(MyGuiScreenMedicals),
                m => m.Name.Contains("<RefreshMedicalRooms>g__AddMedicalRespawnPoints"));
        }

        public static void Postfix(MyGuiScreenMedicals __instance, MyGuiControlTable ___m_respawnsTable, long ___m_restrictedRespawn)
        {
            respawnsTable = ___m_respawnsTable;
            m_restrictedRespawn = ___m_restrictedRespawn;
            
            if (___m_respawnsTable.RowsCount > 0)
            {
                allRows = SortList(___m_respawnsTable.Rows.Where(i => i.UserData is MySpaceRespawnComponent.MyRespawnPointInfo), sortStatus != SortType.None ? sortStatus : SortType.NameAscending);

                long selectedKey = GetSavedState().LastFilterKey;
                if (playersFilterDropdown != null)
                {
                    __instance.RemoveControl(playersFilterDropdown);
                    selectedKey = playersFilterDropdown.GetSelectedKey();
                    playersFilterDropdown.ClearItems();
                }
                else
                {
                    playersFilterDropdown = new MyGuiControlCombobox(new Vector2(0.107f, 0.248f), new Vector2(0.146f, 0.1f));
                    playersFilterDropdown.ItemSelected += PlayersFilterDropdown_ItemSelected;
                }

                playersFilterDropdown.AddItem(0L, "No Filter");
                playersFilterDropdown.AddItem(FriendsFilterKey, FriendsFilterLabel, toolTip: "Me, " + string.Join(", ", GetFriendNames()));

                for (int i = 0; i < allRows.Count; i++)
                {
                    var data = (MySpaceRespawnComponent.MyRespawnPointInfo)allRows[i].UserData;
                    var ownerFaction = MySession.Static.Factions.TryGetPlayerFaction(data.OwnerId);
                    var ownerDisplayName = GetOwnerDisplayName(data.OwnerId);

                    if (playersFilterDropdown.TryGetItemByKey(data.OwnerId) == null)
                    {
                        playersFilterDropdown.AddItem(data.OwnerId, ownerDisplayName, toolTip: ownerFaction != null ? $"{ownerFaction.Tag} {ownerFaction.Name}" : "No Faction");
                    }

                    allRows[i].GetCell(1).Text.Clear().Append(ownerDisplayName);
                }

                playersFilterDropdown.CustomSortItems((x, y) =>
                {
                    if (x.Key == 0) return -1;
                    else if (y.Key == 0) return 1;
                    else if (x.Key == FriendsFilterKey) return -1;
                    else if (y.Key == FriendsFilterKey) return 1;
                    else if (x.Key == MySession.Static.LocalPlayerId) return -1;
                    else if (y.Key == MySession.Static.LocalPlayerId) return 1;
                    else return x.Value.ToString().CompareTo(y.Value.ToString());
                });

                // Saved/previous owner no longer in the list -> default to "Me + friends"
                if (playersFilterDropdown.TryGetItemByKey(selectedKey) == null)
                {
                    selectedKey = FriendsFilterKey;
                }

                // Nothing to show for "Me + friends" (none of you has a spawn) -> fall back to "No Filter"
                if (selectedKey == FriendsFilterKey)
                {
                    HashSet<long> friendIds = GetFriendOwnerIds();
                    bool anyFriendSpawn = allRows.Any(r => r.UserData is MySpaceRespawnComponent.MyRespawnPointInfo d && friendIds.Contains(d.OwnerId));
                    if (!anyFriendSpawn)
                        selectedKey = 0L;
                }

                playersFilterDropdown.SelectItemByKey(selectedKey, false);

                __instance.AddControl(playersFilterDropdown);

                if (searchBox != null) 
                    ApplySearchFilter(___m_respawnsTable, searchBox.SearchText);
                ApplyOwnerFilter(___m_respawnsTable, selectedKey);

                ___m_respawnsTable.SetColumnName(1, new StringBuilder("Owner"));//original: "Available in"
                SortRespawnsTable(___m_respawnsTable, sortStatus != SortType.None ? sortStatus : SortType.NameAscending);

                // Request the auto-select of the last used spawn (done in Update() so the game can't override it)
                if (!autoSelectDone && !autoSelectPending)
                {
                    autoSelectPending = true;
                    autoSelectFramesLeft = 180;
                }
            }
            else
            {
                allRows = null;
            }

        }
    }

    [HarmonyPatch(typeof(MyGuiScreenMedicals), "OnClosed")]
    public static class Patch_MyGuiScreenMedicals_OnClosed
    {
        public static void Postfix(MyGuiScreenMedicals __instance)
        {
            try
            {
                searchBox.OnTextChanged -= OnSearchBoxTextChanged;
                respawnsTable.ItemMouseOver -= OnRespawnTableItemMouseOver;
                respawnsTable.ItemFocus -= OnRespawnTableItemMouseOver;
                respawnsTable.ColumnClicked -= OnRespawnTableColumnClicked;
                playersFilterDropdown.ItemSelected -= PlayersFilterDropdown_ItemSelected;
            }
            catch { }

            searchBox = null;
            respawnsTable = null;
            allRows = null;
            m_restrictedRespawn = -1;
            sortStatus = SortType.None;
            playersFilterDropdown = null;
            friendNames = null;
            savedState = null;
            autoSelectDone = false;
            autoSelectPending = false;
            autoSelectFramesLeft = 0;
            respawnPointTooltip.Clear();

            if (chatHistoryControl != null)
            {
                chatHistoryControl.Position = chatControlPos;
                chatHistoryControl.Size = originalChatSize;
                chatHistoryControl.MouseOverChanged -= Chat_MouseOverChanged;
                chatHistoryControl.Visibility = MyHudControlChat.MyChatVisibilityEnum.AlwaysVisible;
                MyHud.Chat.ChatOpened();
                MyHud.Chat.Update();
                MyHud.Chat.ChatClosed();
            }
        }
    }

    [HarmonyPatch(typeof(MyGuiScreenMedicals), nameof(MyGuiScreenMedicals.RespawnAtMedicalRoom))]
    public static class Patch_MyGuiScreenMedicals_RespawnAtMedicalRoom
    {
        // Remember the spawn you are about to respawn at
        public static void Prefix()
        {
            try
            {
                if (respawnsTable?.SelectedRow?.UserData is MySpaceRespawnComponent.MyRespawnPointInfo info)
                {
                    GetSavedState().LastSpawnId = info.MedicalRoomId;
                    SaveState();
                    Log("Saved last spawn " + info.MedicalRoomId);
                }
            }
            catch { }
        }

        public static void Postfix(MyGuiScreenMedicals __instance)
        {
            if (searchBox != null)
            {
                searchBox.Enabled = false;
                playersFilterDropdown.Enabled = false;

                Vector2 m_size = new Vector2(0.4f, 0.9f);
                __instance.AddControl(new MyGuiControlButton(new Vector2(-0.09f, (float)(m_size.Y / 2.0 - 0.100000001490116)), toolTip: "Try to forcibly close the respawn menu\nand set the camera to your character", text: MyTexts.Get(MySpaceTexts.DetailScreen_Button_Close), onButtonClick: OnAbortBtnClick));
            }
        }
    }

    private static void Chat_MouseOverChanged(MyGuiControlBase control, bool isMouseOver)
    {
        if (isMouseOver)
            MyHud.Chat.ChatOpened();
        else
            MyHud.Chat.ChatClosed();
    }

    private static void OnSearchBoxTextChanged(string newText)
    {
        if (allRows == null || respawnsTable == null)
        {
            return;
        }

        SortListInPlace(allRows, sortStatus);

        ApplySearchFilter(respawnsTable, newText);
        ApplyOwnerFilter(respawnsTable, playersFilterDropdown.GetSelectedKey());
    }

    private static StringBuilder GetOwnerDisplayName(long owner)
    {
        if (owner == 0L)
            return MyTexts.Get(MySpaceTexts.BlockOwner_Nobody);
        MyIdentity identity = Sync.Players.TryGetIdentity(owner);
        return identity != null ? new StringBuilder(identity.DisplayName) : MyTexts.Get(MySpaceTexts.BlockOwner_Unknown);
    }

    private static void OnRespawnTableItemMouseOver(MyGuiControlTable.Row? row)
    {
        respawnPointTooltip.Clear();
        if (row?.UserData is MySpaceRespawnComponent.MyRespawnPointInfo medicalRoom)
        {
            bool isRestricted = m_restrictedRespawn == 0L || medicalRoom.MedicalRoomId == m_restrictedRespawn;
            respawnPointTooltip.AppendLine(medicalRoom.MedicalRoomName);
            respawnPointTooltip.Append("Status: " + (isRestricted ? MyTexts.Get(MySpaceTexts.ScreenMedicals_RespawnShipReady) : MyTexts.Get(MySpaceTexts.ScreenMedicals_RespawnShipNotReady)));
            respawnsTable?.SetToolTip(respawnPointTooltip.ToString());
        }
        else
        {
            respawnsTable?.HideToolTip();
        }
    }

    private static void OnRespawnTableColumnClicked(MyGuiControlTable table, int columnIndex)
    {
        if (columnIndex == 0)
        {
            if (sortStatus == SortType.NameAscending)
            {
                SortRespawnsTable(table, SortType.NameDescending);
            }
            else
            {
                SortRespawnsTable(table, SortType.NameAscending);
            }
        }
        else if (columnIndex == 1)
        {
            if (sortStatus == SortType.OwnerAscending)
            {
                SortRespawnsTable(table, SortType.OwnerDescending);
            }
            else
            {
                SortRespawnsTable(table, SortType.OwnerAscending);
            }
        }
    }

    private static void SortRespawnsTable(MyGuiControlTable table, SortType type)
    {
        if (table == null || type == SortType.None)
        {
            return;
        }

        MyGuiControlTable.Row selectedRow = table.SelectedRow;
        sortStatus = type;
        List<MyGuiControlTable.Row> ordered = SortList(table.Rows.Where(i => i.UserData is MySpaceRespawnComponent.MyRespawnPointInfo), type);
        SortInternal();

        void SortInternal()
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                table.Remove(ordered[i]);
                table.Insert(table.RowsCount, ordered[i]);
            }
            if (selectedRow != null && table.Rows.Contains(selectedRow))
            {
                table.SelectedRow = selectedRow;
            }
        }
    }

    private static List<MyGuiControlTable.Row> SortList(IEnumerable<MyGuiControlTable.Row> list, SortType type)
    {
        if (type == SortType.NameAscending)
        {
            return list.OrderBy(i => i.GetCell(0).Text.ToString()).ToList();
        }
        else if (type == SortType.NameDescending)
        {
            return list.OrderByDescending(i => i.GetCell(0).Text.ToString()).ToList();
        }
        else if (type == SortType.OwnerAscending)
        {
            return list.OrderBy(i => i.GetCell(1).Text.ToString()).ThenBy(i => i.GetCell(0).Text.ToString()).ToList();
        }
        else if (type == SortType.OwnerDescending)
        {
            return list.OrderByDescending(i => i.GetCell(1).Text.ToString()).ThenByDescending(i => i.GetCell(0).Text.ToString()).ToList();
        }
        else
        {
            return list.ToList();
        }
    }

    private static void SortListInPlace(List<MyGuiControlTable.Row> list, SortType type)
    {
        if (type == SortType.NameAscending)
        {
            list.Sort((x, y) => x.GetCell(0).Text.ToString().CompareTo(y.GetCell(0).Text.ToString()));
        }
        else if (type == SortType.NameDescending)
        {
            list.Sort((y, x) => x.GetCell(0).Text.ToString().CompareTo(y.GetCell(0).Text.ToString()));
        }
        else if (type == SortType.OwnerAscending)
        {
            list.Sort((x, y) =>
            {
                int result = x.GetCell(1).Text.ToString().CompareTo(y.GetCell(1).Text.ToString());
                if (result != 0) return result;
                else return x.GetCell(0).Text.ToString().CompareTo(y.GetCell(0).Text.ToString());
            });
        }
        else if (type == SortType.OwnerDescending)
        {
            list.Sort((y, x) =>
            {
                int result = x.GetCell(1).Text.ToString().CompareTo(y.GetCell(1).Text.ToString());
                if (result != 0) return result;
                else return x.GetCell(0).Text.ToString().CompareTo(y.GetCell(0).Text.ToString());
            });
        }
    }

    private static void PlayersFilterDropdown_ItemSelected()
    {
        long selectedKey = playersFilterDropdown.GetSelectedKey();

        // Remember the chosen filter for next time
        GetSavedState().LastFilterKey = selectedKey;
        SaveState();

        if (searchBox != null)
            ApplySearchFilter(respawnsTable, searchBox.SearchText);
        ApplyOwnerFilter(respawnsTable, selectedKey);
    }

    private static void ApplySearchFilter(MyGuiControlTable table, string searchTerm)
    {
        MyGuiControlTable.Row selectedRow = respawnsTable.SelectedRow;
        if (String.IsNullOrWhiteSpace(searchTerm))
        {
            for (int i = 0; i < allRows.Count; i++)
            {
                if (table.Rows.Contains(allRows[i]))
                {
                    table.Remove(allRows[i]);
                }
                table.Insert(table.Rows.Count, allRows[i]);
            }

            if (selectedRow != null && table.Rows.Contains(selectedRow))
            {
                table.SelectedRow = selectedRow;
            }
        }
        else
        {
            for (int i = 0; i < allRows.Count; i++)
            {
                if (table.Rows.Contains(allRows[i]))
                {
                    table.Remove(allRows[i]);
                }
                if (allRows[i].GetCell(0).Text.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    table.Insert(table.Rows.Count, allRows[i]);
                }
            }
        }

        if (selectedRow != null && table.Rows.Contains(selectedRow))
        {
            table.SelectedRow = selectedRow;
        }
    }

    private static void ApplyOwnerFilter(MyGuiControlTable table, long ownerId)
    {
        if (ownerId == 0)
        {
            return;
        }

        HashSet<long> allowedOwners = ownerId == FriendsFilterKey ? GetFriendOwnerIds() : new HashSet<long> { ownerId };

        for (int i = 0; i < table.RowsCount;)
        {
            if (table.Rows[i].UserData is MySpaceRespawnComponent.MyRespawnPointInfo userData && !allowedOwners.Contains(userData.OwnerId))
            {
                table.Remove(table.Rows[i]);
            }
            else i++;
        }
    }

    public static void OnAbortBtnClick(MyGuiControlButton btn)
    {
        if (MyAPIGateway.Session.LocalHumanPlayer?.Character != null)
        {
            MyAPIGateway.Session.SetCameraController(MyCameraControllerEnum.Entity, MyAPIGateway.Session.LocalHumanPlayer.Character);
            MySpaceRespawnComponent.Static.CloseRespawnScreen();
            btn.Enabled = false;
        }
    }
}