using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// This plugin is the entry point for QuickStack.
// BepInEx creates this class when Valheim loads the mod.
[BepInPlugin("quickstack", "QuickStack", "1.3.0")]
public sealed class QuickStackPlugin : BaseUnityPlugin
{
	// This cache stores containers currently loaded by this game client.
	// A HashSet prevents duplicate entries and supports fast removal.
	private static readonly HashSet<Container> loadedContainers = new HashSet<Container>();
	private static readonly MethodInfo containerSaveMethod =
	AccessTools.Method(typeof(Container), "Save");

	// This prototype stores favorite inventory slots by grid position.
	private readonly HashSet<Vector2i> favoriteSlots = new HashSet<Vector2i>();

	// This set stores item identities so protection follows items between slots.
	private readonly HashSet<string> favoriteItemIdentities = new HashSet<string>();

	// This config entry stores favorite positions across game restarts.
	private ConfigEntry<string> favoriteSlotConfig = null!;

	// This config entry stores favorite item identities across game restarts.
	private ConfigEntry<string> favoriteItemConfig = null!;

	// These settings control the border colors of favorites (RGBA).
	// The defaults match the colors used before these settings existed.
	private static readonly Color DefaultSlotFavoriteColor = new Color32(226, 169, 101, 179);
	private static readonly Color DefaultItemFavoriteColor = new Color32(28, 146, 216, 179);
	private ConfigEntry<Color> slotFavoriteColor = null!;
	private ConfigEntry<Color> itemFavoriteColor = null!;

	// This setting lets players change the hotkey without rebuilding the mod.
	private ConfigEntry<KeyboardShortcut> quickStackHotkey = null!;

	// These settings add a controller shortcut: hold the modifier, press the button.
	private ConfigEntry<string> quickStackGamepadModifier = null!;
	private ConfigEntry<string> quickStackGamepadButton = null!;
	private GamepadCombo? gamepadCombo;

	// These settings choose the favorite shortcuts. The keyboard shortcut may use a mouse
	// button as its main key (Mouse0 = left click, Mouse1 = right click).
	// They are static because the click-suppression patches read them.
	private static ConfigEntry<KeyboardShortcut> favoriteItemHotkey = null!;
	private static ConfigEntry<KeyboardShortcut> favoriteSlotHotkey = null!;
	private GamepadCombo? favoriteItemGamepadCombo;
	private GamepadCombo? favoriteSlotGamepadCombo;
	private static float lastFavoriteItemKeyTime = -10f;
	private static float lastFavoriteSlotKeyTime = -10f;
	private bool warnedNoGamepadSelection;

	// This setting controls how far QuickStack searches for containers.
	private ConfigEntry<float> quickStackRange = null!;

	// These settings protect commonly used player items from automatic transfer.
	private ConfigEntry<bool> skipEquippedItems = null!;
	private ConfigEntry<bool> skipHotbarItems = null!;
	private ConfigEntry<bool> skipShipContainers = null!;
	private static Container? openedContainer;

	// Awake runs once when BepInEx creates the plugin.
	// Use it for setup that should happen one time.
	private void Awake()
	{
		KeyboardHotkey.Log = Logger;

		// Store the hotkey in BepInEx configuration.
		// Backquote becomes the default only when no saved setting exists yet.
		// A combo works too: hold the modifier key(s), then press the main key.
		quickStackHotkey = Config.Bind(
			"General",
			"Quick stack hotkey",
			new KeyboardShortcut(KeyCode.BackQuote),
			"Keyboard shortcut that quick-stacks items. Supports a two-key combo: " +
			"the last key is pressed while the other key is held " +
			"(for example LeftControl + Q). A single key also works.");

		// Controller shortcut: hold the modifier button, then press the button.
		quickStackGamepadModifier = Config.Bind(
			"General",
			"Quick stack gamepad modifier",
			"BumperL",
			"Gamepad button that must be held for the quick stack shortcut " +
			"(BumperL = LB). Set to None to use a single button. Valid values: " +
			GamepadCombo.ValidNames);

		quickStackGamepadButton = Config.Bind(
			"General",
			"Quick stack gamepad button",
			"StickR",
			"Gamepad button that quick-stacks items. Set to None to disable the " +
			"gamepad shortcut. Valid values: " +
			GamepadCombo.ValidNames);

		gamepadCombo = new GamepadCombo(
			quickStackGamepadModifier,
			quickStackGamepadButton,
			Logger);

		// Store range in config so players can tune it without rebuilding the mod.
		quickStackRange = Config.Bind(
			"General",
			"Container range",
			20f,
			"Maximum distance from player for nearby containers.");

		skipEquippedItems = Config.Bind(
			"General",
			"Skip equipped items",
			true,
			"Do not move items currently equipped by the player.");

		skipHotbarItems = Config.Bind(
			"General",
			"Skip hotbar items",
			true,
			"Do not move items assigned to the hotbar.");

		skipShipContainers = Config.Bind(
			"General",
			"Skip ship containers",
			true,
			"Do not move items into ship containers. Recommended for multiplayer safety.");

		// Store favorite coordinates as text because BepInEx config has no Vector2i type.
		favoriteSlotConfig = Config.Bind(
			"Favorites",
			"Slots",
			string.Empty,
			"Favorite player inventory slots, stored as x:y pairs separated by semicolons.");
		favoriteItemConfig = Config.Bind(
			"Favorites",
			"Items",
			string.Empty,
			"Favorite item identities, separated by semicolons.");

		// Favorite shortcuts. Defaults: Alt + left-click favorites the slot,
		// Alt + right-click favorites the item type.
		favoriteItemHotkey = Config.Bind(
			"Favorites",
			"Favorite item hotkey",
			new KeyboardShortcut(KeyCode.Mouse1, KeyCode.LeftAlt),
			"Hover an inventory slot and press this to favorite the item there (protects that " +
			"item type). Format: modifier + main key, for example LeftAlt + Mouse1 (right click) " +
			"or LeftControl + F. Mouse buttons are Mouse0 to Mouse6. Keep a modifier on mouse " +
			"shortcuts, otherwise the normal click stops working. Left and right Alt, Shift and " +
			"Ctrl are treated the same.");

		favoriteSlotHotkey = Config.Bind(
			"Favorites",
			"Favorite slot hotkey",
			new KeyboardShortcut(KeyCode.Mouse0, KeyCode.LeftAlt),
			"Hover an inventory slot and press this to favorite the slot itself, whatever item " +
			"is in it. Same format as the item hotkey, for example LeftAlt + Mouse0 (left click).");

		ConfigEntry<string> favoriteItemPadModifier = Config.Bind(
			"Favorites",
			"Favorite item gamepad modifier",
			"TriggerL",
			"Gamepad button that must be held for the favorite item shortcut. " +
			"Set to None to use a single button. Valid values: " +
			GamepadCombo.ValidNames);

		ConfigEntry<string> favoriteItemPadButton = Config.Bind(
			"Favorites",
			"Favorite item gamepad button",
			"StickR",
			"Gamepad button that favorites the item under the gamepad selection " +
			"(inventory must be open). Set to None to disable. Valid values: " +
			GamepadCombo.ValidNames);

		ConfigEntry<string> favoriteSlotPadModifier = Config.Bind(
			"Favorites",
			"Favorite slot gamepad modifier",
			"TriggerL",
			"Gamepad button that must be held for the favorite slot shortcut. " +
			"Set to None to use a single button. Valid values: " +
			GamepadCombo.ValidNames);

		ConfigEntry<string> favoriteSlotPadButton = Config.Bind(
			"Favorites",
			"Favorite slot gamepad button",
			"StickL",
			"Gamepad button that favorites the slot under the gamepad selection " +
			"(inventory must be open). Set to None to disable. Valid values: " +
			GamepadCombo.ValidNames);

		favoriteItemGamepadCombo = new GamepadCombo(favoriteItemPadModifier, favoriteItemPadButton, Logger);
		favoriteSlotGamepadCombo = new GamepadCombo(favoriteSlotPadModifier, favoriteSlotPadButton, Logger);

		// Colors are stored as RGBA hex (for example E2A965B3).
		// The last two digits are the transparency.
		slotFavoriteColor = Config.Bind(
			"Favorites",
			"Slot color",
			DefaultSlotFavoriteColor,
			"Border color of favorite slots, as RGBA hex.");
		itemFavoriteColor = Config.Bind(
			"Favorites",
			"Item color",
			DefaultItemFavoriteColor,
			"Border color of favorite items, as RGBA hex.");
		LoadFavoriteSlots();
		LoadFavoriteItems();

		// Harmony lets this plugin add small methods before or after Valheim methods.
		new Harmony("quickstack").PatchAll();
		// Persist newly added settings immediately so users can edit the config file.
		Config.Save();

		// This confirms that BepInEx loaded the plugin successfully.
		Logger.LogInfo(
			$"QuickStack loaded. Keyboard: {quickStackHotkey.Value}. " +
			$"Gamepad: {quickStackGamepadModifier.Value} + {quickStackGamepadButton.Value}.");
	}

	// Update runs once every rendered frame while the game is running.
	// Keep this check small because it runs many times per second.
	private void Update()
	{
		// Do not react while the player types (chat, console, sign text, map pin name).
		if (!IsTextInputActive())
		{
			// Every source is evaluated each frame so keyboard and gamepad can be
			// swapped freely. The keyboard is read through legacy input and through
			// the Input System, in case the game suppresses one of them while a
			// gamepad is active. The non-short-circuit | keeps all sources polled.
			bool quickStackPressed =
				IsShortcutDown(quickStackHotkey.Value) |
				(gamepadCombo != null && gamepadCombo.WasPressed());

			if (Debounce(ref lastQuickStackTime, quickStackPressed))
			{
				// Keep input detection separate from inventory behavior.
				RunQuickStack();
			}

			HandleFavoriteHotkeys();
		}

		// Inventory UI can recreate slot elements after loading or refreshes.
		// Reapply missing borders while the player inventory is visible.
		ApplyFavoriteBorders();
	}

	// Returns true only on the frame when the main key is pressed while all modifiers are held.
	// Keys are read through legacy input and the Input System (mouse buttons: legacy only).
	private static bool IsShortcutDown(KeyboardShortcut shortcut)
	{
		KeyCode main = shortcut.MainKey;
		if (main == KeyCode.None) return false;

		if (!Input.GetKeyDown(main) && !KeyboardHotkey.WasPressedThisFrame(main)) return false;

		foreach (KeyCode modifier in shortcut.Modifiers)
		{
			if (!IsModifierHeld(modifier)) return false;
		}

		return true;
	}

	// Left and right Alt, Shift and Ctrl count as the same modifier.
	private static bool IsModifierHeld(KeyCode key)
	{
		KeyCode other = OtherSide(key);
		return Input.GetKey(key) || Input.GetKey(other) ||
			KeyboardHotkey.IsHeld(key) || KeyboardHotkey.IsHeld(other);
	}

	private static KeyCode OtherSide(KeyCode key)
	{
		switch (key)
		{
			case KeyCode.LeftAlt: return KeyCode.RightAlt;
			case KeyCode.RightAlt: return KeyCode.LeftAlt;
			case KeyCode.LeftShift: return KeyCode.RightShift;
			case KeyCode.RightShift: return KeyCode.LeftShift;
			case KeyCode.LeftControl: return KeyCode.RightControl;
			case KeyCode.RightControl: return KeyCode.LeftControl;
			default: return KeyCode.None;
		}
	}

	private static bool IsMouseKey(KeyCode key)
	{
		return key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6;
	}

	private static float lastQuickStackTime = -10f;

	// The same key press can be reported by legacy input and by the Input System
	// in slightly different frames. Ignore repeats within 0.25 s.
	private static bool Debounce(ref float lastTime, bool pressed)
	{
		if (!pressed) return false;

		float now = Time.unscaledTime;
		if (now - lastTime < 0.25f) return false;

		lastTime = now;
		return true;
	}

	private static bool IsTextInputActive()
	{
		return (Chat.instance != null && Chat.instance.HasFocus()) ||
			global::Console.IsVisible() ||
			TextInput.IsVisible() ||
			Minimap.InTextInput();
	}

	// This method checks both favorite shortcuts (keyboard/mouse and gamepad) each frame.
	private void HandleFavoriteHotkeys()
	{
		CheckFavoriteInput(favoriteItemHotkey.Value, favoriteItemGamepadCombo, ref lastFavoriteItemKeyTime, false);
		CheckFavoriteInput(favoriteSlotHotkey.Value, favoriteSlotGamepadCombo, ref lastFavoriteSlotKeyTime, true);
	}

	private void CheckFavoriteInput(
		KeyboardShortcut shortcut,
		GamepadCombo? combo,
		ref float lastKeyTime,
		bool slotMode)
	{
		// Mouse buttons come from one source only, so they need no debounce.
		bool keyboardPressed = IsMouseKey(shortcut.MainKey)
			? IsShortcutDown(shortcut)
			: Debounce(ref lastKeyTime, IsShortcutDown(shortcut));
		bool gamepadPressed = combo != null && combo.WasPressed();

		if (gamepadPressed)
		{
			HandleFavoriteToggle(slotMode, true);
		}
		else if (keyboardPressed)
		{
			HandleFavoriteToggle(slotMode, false);
		}
	}

	// This method toggles item or slot protection for the slot under the mouse cursor,
	// or under the gamepad selection when the shortcut came from the controller.
	private void HandleFavoriteToggle(bool slotMode, bool fromGamepad)
	{
		if (InventoryGui.instance == null ||
			InventoryGui.instance.m_playerGrid == null ||
			!InventoryGui.IsVisible())
		{
			// Gamepad buttons are used during normal play too, so stay quiet for them.
			if (!fromGamepad)
			{
				Logger.LogInfo("Player inventory is not open.");
			}
			return;
		}

		InventoryGrid grid = InventoryGui.instance.m_playerGrid;

		Vector2i selected = new Vector2i(-1, -1);
		if (fromGamepad && !TryGetGamepadSelection(grid, out selected))
		{
			return;
		}

		// Mouse mode checks each visible inventory element against the mouse position.
		// Gamepad mode compares element positions with the gamepad selection instead.
		FieldInfo elementsField = typeof(InventoryGrid).GetField(
			"m_elements",
			BindingFlags.Instance | BindingFlags.NonPublic)!;
		PropertyInfo positionProperty = typeof(InventoryElement).GetProperty(
			"Position",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
		List<InventoryElement> elements = (List<InventoryElement>)elementsField.GetValue(grid)!;

		foreach (InventoryElement element in elements)
		{
			Vector2i position = (Vector2i)positionProperty.GetValue(element, null)!;
			bool hit = fromGamepad
				? position.x == selected.x && position.y == selected.y
				: RectTransformUtility.RectangleContainsScreenPoint(
					element.GetComponent<RectTransform>(),
					Input.mousePosition,
					null);

			if (!hit)
			{
				continue;
			}

			ItemDrop.ItemData item = GetPlayerItemAt(position);
			if (slotMode)
			{
				ToggleFavoriteSlot(element, position);
			}
			else if (item != null)
			{
				ToggleFavoriteItem(element, item, position);
			}
			return;
		}

		Logger.LogInfo(fromGamepad
			? "Gamepad selection is not on a player inventory slot."
			: "Mouse is not over a player inventory slot.");
	}

	// This method reads which player-grid slot the gamepad has selected.
	// The member names differ between Valheim versions, so try each known one.
	private bool TryGetGamepadSelection(InventoryGrid grid, out Vector2i position)
	{
		position = new Vector2i(-1, -1);
		BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		// Ignore the stale selection when the gamepad is focused on another grid.
		object? group = typeof(InventoryGrid).GetField("m_uiGroup", flags)?.GetValue(grid);
		if (group != null &&
			group.GetType().GetProperty("IsActive", flags)?.GetValue(group, null) is bool active &&
			!active)
		{
			return false;
		}

		object? value =
			typeof(InventoryGrid).GetProperty("SelectionGridPosition", flags)?.GetValue(grid, null) ??
			typeof(InventoryGrid).GetMethod("GetSelectedGridPosition", flags, null, System.Type.EmptyTypes, null)?.Invoke(grid, null) ??
			typeof(InventoryGrid).GetField("m_selected", flags)?.GetValue(grid);

		if (value is Vector2i found)
		{
			position = found;
			return true;
		}

		if (!warnedNoGamepadSelection)
		{
			warnedNoGamepadSelection = true;
			Logger.LogWarning("Could not read the gamepad selection from InventoryGrid. Gamepad favorites will not work.");
		}

		return false;
	}

	// This method reads item data from player inventory instead of UI state.
	private ItemDrop.ItemData GetPlayerItemAt(Vector2i position)
	{
		if (Player.m_localPlayer == null)
		{
			return null!;
		}

		Inventory inventory = Player.m_localPlayer.GetInventory();
		return inventory == null ? null! : inventory.GetItemAt(position.x, position.y);
	}

	// This method toggles identity protection for an item under the cursor.
	private void ToggleFavoriteItem(InventoryElement element, ItemDrop.ItemData item, Vector2i position)
	{
		string identity = GetItemIdentity(item);
		if (favoriteItemIdentities.Remove(identity))
		{
			RefreshFavoriteBorder(element, item, position);
			SaveFavoriteItems();
			return;
		}

		favoriteItemIdentities.Add(identity);
		RefreshFavoriteBorder(element, item, position);
		SaveFavoriteItems();
	}

	// This method toggles a slot favorite and updates its visual border.
	private void ToggleFavoriteSlot(InventoryElement element, Vector2i position)
	{
		if (favoriteSlots.Contains(position))
		{
			// A saved favorite may have lost only its visual border after UI rebuild.
			// Restore border on first click instead of accidentally removing favorite.
			if (element.transform.Find("QuickStackFavoriteBorder") == null)
			{
				ItemDrop.ItemData existingItem = GetPlayerItemAt(position);
				RefreshFavoriteBorder(element, existingItem, position);
				return;
			}

			favoriteSlots.Remove(position);
			ItemDrop.ItemData remainingItem = GetPlayerItemAt(position);
			RefreshFavoriteBorder(element, remainingItem, position);
			SaveFavoriteSlots();
			return;
		}

		favoriteSlots.Add(position);
		ItemDrop.ItemData item = GetPlayerItemAt(position);
		RefreshFavoriteBorder(element, item, position);
		SaveFavoriteSlots();
	}

	// This method keeps one border synchronized with both favorite systems.
	private void RefreshFavoriteBorder(InventoryElement element, ItemDrop.ItemData item, Vector2i position)
	{
		if (favoriteSlots.Contains(position))
		{
			AddFavoriteBorder(element, false);
		}
		else if (item != null && favoriteItemIdentities.Contains(GetItemIdentity(item)))
		{
			AddFavoriteBorder(element, true);
		}
		else
		{
			RemoveFavoriteBorder(element);
		}
	}

	// This method restores borders for favorite slots whose UI elements were recreated.
	private void ApplyFavoriteBorders()
	{
		if (InventoryGui.instance == null || InventoryGui.instance.m_playerGrid == null)
		{
			return;
		}

		FieldInfo elementsField = typeof(InventoryGrid).GetField(
			"m_elements",
			BindingFlags.Instance | BindingFlags.NonPublic)!;
		PropertyInfo positionProperty = typeof(InventoryElement).GetProperty(
			"Position",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
		List<InventoryElement> elements = (List<InventoryElement>)elementsField.GetValue(
			InventoryGui.instance.m_playerGrid)!;

		foreach (InventoryElement element in elements)
		{
			Vector2i position = (Vector2i)positionProperty.GetValue(element, null)!;
			ItemDrop.ItemData item = GetPlayerItemAt(position);
			RefreshFavoriteBorder(element, item, position);
		}
	}

	// This method gives same item type, quality, and variant one persistent identity.
	private string GetItemIdentity(ItemDrop.ItemData item)
	{
		return $"{item.m_shared.m_name}|{item.m_quality}|{item.m_variant}";
	}

	// This method restores favorite slot positions from BepInEx config at startup.
	private void LoadFavoriteSlots()
	{
		favoriteSlots.Clear();
		foreach (string entry in favoriteSlotConfig.Value.Split(';'))
		{
			string[] coordinates = entry.Split(':');
			if (coordinates.Length != 2 ||
				!int.TryParse(coordinates[0], out int x) ||
				!int.TryParse(coordinates[1], out int y))
			{
				continue;
			}

			favoriteSlots.Add(new Vector2i(x, y));
		}
	}

	// This method restores identity favorites from BepInEx config.
	private void LoadFavoriteItems()
	{
		favoriteItemIdentities.Clear();
		foreach (string identity in favoriteItemConfig.Value.Split(';'))
		{
			if (!string.IsNullOrWhiteSpace(identity))
			{
				favoriteItemIdentities.Add(identity);
			}
		}
	}

	// This method writes favorite slot positions immediately after each toggle.
	private void SaveFavoriteSlots()
	{
		List<string> entries = new List<string>();
		foreach (Vector2i position in favoriteSlots)
		{
			entries.Add($"{position.x}:{position.y}");
		}

		favoriteSlotConfig.Value = string.Join(";", entries);
		Config.Save();
	}

	// This method writes identity favorites immediately after each toggle.
	private void SaveFavoriteItems()
	{
		favoriteItemConfig.Value = string.Join(";", favoriteItemIdentities);
		Config.Save();
	}

	private static void SaveContainer(Container container)
	{
		containerSaveMethod.Invoke(container, null);
	}

	// This method creates four thin UI images instead of covering the item with a solid color.
	private void AddFavoriteBorder(InventoryElement element, bool identityMode = false)
	{
		Transform existingBorder = element.transform.Find("QuickStackFavoriteBorder");
		// Read from config every time so color changes apply without restarting.
		Color borderColor = identityMode ? itemFavoriteColor.Value : slotFavoriteColor.Value;
		if (existingBorder != null)
		{
			Image existingImage = existingBorder.GetComponent<Image>();
			if (existingImage != null)
			{
				existingImage.color = borderColor;
			}
			return;
		}

		GameObject border = new GameObject("QuickStackFavoriteBorder");
		border.transform.SetParent(element.transform, false);
		RectTransform borderRect = border.AddComponent<RectTransform>();
		borderRect.anchorMin = Vector2.zero;
		borderRect.anchorMax = Vector2.one;
		borderRect.offsetMin = Vector2.zero;
		borderRect.offsetMax = Vector2.zero;
		borderRect.SetAsLastSibling();

		Image image = border.AddComponent<Image>();
		image.sprite = CreateRoundedBorderSprite();
		image.type = Image.Type.Sliced;
		image.color = borderColor;
		image.raycastTarget = false;
	}

	// This method creates a small rounded-corner sprite for the favorite border.
	private Sprite CreateRoundedBorderSprite()
	{
		Texture2D texture = new Texture2D(9, 9, TextureFormat.RGBA32, false);
		for (int y = 0; y < 9; y++)
		{
			for (int x = 0; x < 9; x++)
			{
				bool insideOuter = IsInsideRoundedRectangle(x, y, 0, 8, 0, 8, 2.5f);
				// Keep a transparent 3x3 center, creating a visibly thicker frame.
				bool insideInner = x >= 3 && x <= 5 && y >= 3 && y <= 5;
				texture.SetPixel(x, y, insideOuter && !insideInner ? Color.white : Color.clear);
			}
		}
		texture.Apply();
		return Sprite.Create(texture, new Rect(0f, 0f, 9f, 9f), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(3f, 3f, 3f, 3f));
	}

	// This method tests rounded rectangle membership for one texture pixel.
	private bool IsInsideRoundedRectangle(int x, int y, int minX, int maxX, int minY, int maxY, float radius)
	{
		int cornerX = x < minX + radius ? minX + 2 : maxX - 2;
		int cornerY = y < minY + radius ? minY + 2 : maxY - 2;
		bool inCorner = (x < minX + radius || x > maxX - radius) &&
			(y < minY + radius || y > maxY - radius);
		if (!inCorner)
		{
			return x >= minX && x <= maxX && y >= minY && y <= maxY;
		}

		float deltaX = x - cornerX;
		float deltaY = y - cornerY;
		return deltaX * deltaX + deltaY * deltaY <= radius * radius;
	}

	// This method removes the visual border while keeping favorite state for the slot offscreen.
	private void RemoveFavoriteBorder(InventoryElement element)
	{
		Transform border = element.transform.Find("QuickStackFavoriteBorder");
		if (border != null)
		{
			Destroy(border.gameObject);
		}
	}

	// This method will eventually move matching player items into nearby chests.
	private void RunQuickStack()
	{
		// Valheim has no local player during menus and some loading screens.
		// Stop here instead of trying to read a missing player object.
		if (Player.m_localPlayer == null)
		{
			Logger.LogWarning("QuickStack cannot run because no local player exists.");
			return;
		}

		// Read player position first. Nearby chest search will use this position later.
		Vector3 playerPosition = Player.m_localPlayer.transform.position;
		Logger.LogInfo($"QuickStack ran at position {playerPosition}.");
		Inventory playerInventory = Player.m_localPlayer.GetInventory();
		if (playerInventory == null)
		{
			Logger.LogWarning("Local player has no inventory.");
			return;
		}

		List<ItemDrop.ItemData> playerItems = GetPlayerItems(Player.m_localPlayer);

		// Use the cache instead of searching every loaded object on every key press.
		int nearbyContainerCount = 0;
		List<Container> staleContainers = new List<Container>();
		int movedItemCount = 0;
		foreach (Container container in loadedContainers)
		{
			// Destroyed objects can remain in a cache until their removal patch runs.
			// Remove them now so repeated scans do not keep checking stale entries.
			if (container == null)
			{
				staleContainers.Add(container!);
				continue;
			}

			float distance = Vector3.Distance(playerPosition, container.transform.position);
			if (distance <= quickStackRange.Value &&
				(!skipShipContainers.Value || !IsShipContainer(container)) &&
				(!container.IsInUse() || IsOpenedByLocalPlayer(container)))
			{
				nearbyContainerCount++;
				movedItemCount += MoveMatchingItems(container, playerInventory, playerItems);
			}
		}
		loadedContainers.ExceptWith(staleContainers);

		if (nearbyContainerCount == 0)
		{
			Logger.LogInfo($"No containers found within {quickStackRange.Value:F1}m.");
		}
		else if (movedItemCount > 0)
		{
			MessageHud.instance.ShowMessage(
				MessageHud.MessageType.Center,
				$"Stacked {movedItemCount} items");
		}
	}

	// This method returns player inventory contents without changing any items.
	private List<ItemDrop.ItemData> GetPlayerItems(Player player)
	{
		Inventory inventory = player.GetInventory();
		if (inventory == null)
		{
			Logger.LogWarning("Local player has no inventory.");
			return new List<ItemDrop.ItemData>();
		}

		// Player and chest inventories use the same ItemData type.
		// This shared shape lets quick-stack compare item names later.
		List<ItemDrop.ItemData> items = inventory.GetAllItems();
		if (items.Count == 0)
		{
			Logger.LogInfo("Player inventory is empty.");
			return items;
		}

		return items;
	}

	// This method moves matching player stacks into one nearby container.
	private int MoveMatchingItems(
		Container container,
		Inventory playerInventory,
		List<ItemDrop.ItemData> playerItems)
	{
		Inventory inventory = container.GetInventory();
		if (inventory == null)
		{
			Logger.LogWarning($"Container {container.name} has no inventory.");
			return 0;
		}

		// Container state can change after the nearby-container scan.
		// Recheck both multiplayer safety conditions immediately before transfer.
		if ((skipShipContainers.Value && IsShipContainer(container)) || (container.IsInUse() && !IsOpenedByLocalPlayer(container)))
		{
			return 0;
		}

		int movedItemCount = 0;
		List<ItemDrop.ItemData> equippedItems = playerInventory.GetEquippedItems();
		HashSet<ItemDrop.ItemData> hotbarItems = new HashSet<ItemDrop.ItemData>(
			playerInventory.GetHotbar(false));
		// Inventory transfer changes the live player item list.
		// Iterate over a snapshot so the loop collection stays unchanged.
		List<ItemDrop.ItemData> itemsToMove = new List<ItemDrop.ItemData>(playerItems);

		// Check each player item against the chest before changing either inventory.
		foreach (ItemDrop.ItemData playerItem in itemsToMove)
		{
			// Another player may open container while this loop is running.
			if ((skipShipContainers.Value && IsShipContainer(container)) || (container.IsInUse() && !IsOpenedByLocalPlayer(container)))
			{
				break;
			}

			if (playerItem.m_stack <= 0)
			{
				continue;
			}

			if (skipEquippedItems.Value && equippedItems.Contains(playerItem))
			{
				continue;
			}

			if (skipHotbarItems.Value && hotbarItems.Contains(playerItem))
			{
				continue;
			}

			// Favorite slots are protected. Read current grid position from ItemData
			// so moving other items cannot make this check depend on UI elements.
			if (favoriteSlots.Contains(playerItem.m_gridPos))
			{
				continue;
			}

			if (favoriteItemIdentities.Contains(GetItemIdentity(playerItem)))
			{
				continue;
			}

			// FindFreeStackSpace returns zero both when no matching item exists and
			// when a matching item exists but has no available stack space.
			// Check for the matching item first so the log explains the real problem.
			// Skip items with no matching stack in this chest at all — QuickStack only tops off existing types.
			bool hasMatch = false;
			foreach (ItemDrop.ItemData containerItem in inventory.GetAllItems())
			{
				if (containerItem.m_shared.m_name == playerItem.m_shared.m_name)
				{
					hasMatch = true;
					break;
				}
			}
			if (!hasMatch)
			{
				continue;
			}

			int remaining = playerItem.m_stack;

			// Fill every existing partial stack of this item first, each up to its own remaining room.
			foreach (ItemDrop.ItemData containerItem in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
			{
				if (remaining <= 0) break;
				if (containerItem.m_shared.m_name != playerItem.m_shared.m_name) continue;

				int room = containerItem.m_shared.m_maxStackSize - containerItem.m_stack;
				if (room <= 0) continue;

				int amount = Mathf.Min(room, remaining);
				inventory.MoveItemToThis(playerInventory, playerItem, amount, containerItem.m_gridPos.x, containerItem.m_gridPos.y);
				remaining -= amount;
				movedItemCount += amount;
			}

			// Then spill any leftover into empty slots, since this item type already exists in the chest.
			while (remaining > 0 && TryFindEmptySlot(inventory, out Vector2i emptySlot))
			{
				int amount = Mathf.Min(playerItem.m_shared.m_maxStackSize, remaining);
				inventory.MoveItemToThis(playerInventory, playerItem, amount, emptySlot.x, emptySlot.y);
				remaining -= amount;
				movedItemCount += amount;
			}
		}

		if (movedItemCount > 0)
		{
			SaveContainer(container);
		}

		return movedItemCount;
	}

	// This method finds an empty grid position through public Inventory methods.
	private bool TryFindEmptySlot(Inventory inventory, out Vector2i position)
	{
		for (int y = 0; y < inventory.GetHeight(); y++)
		{
			for (int x = 0; x < inventory.GetWidth(); x++)
			{
				if (inventory.GetItemAt(x, y) == null)
				{
					position = new Vector2i(x, y);
					return true;
				}
			}
		}

		position = new Vector2i(-1, -1);
		return false;
	}

	// This method counts matching stack space plus empty inventory slots.
	private int GetAvailableSpace(Inventory inventory, ItemDrop.ItemData playerItem)
	{
		int availableSpace = 0;
		List<ItemDrop.ItemData> containerItems = inventory.GetAllItems();
		foreach (ItemDrop.ItemData containerItem in containerItems)
		{
			if (containerItem.m_shared.m_name == playerItem.m_shared.m_name)
			{
				availableSpace += containerItem.m_shared.m_maxStackSize - containerItem.m_stack;
			}
		}

		availableSpace += inventory.GetEmptySlots() * playerItem.m_shared.m_maxStackSize;
		return availableSpace;
	}

	// Ship cargo is a Container component that lives as a child of the Ship object,
	// not a Container subtype — "container is Ship" is always false and never
	// excludes anything. Walk up the hierarchy instead.
	private static bool IsShipContainer(Container container)
	{
		return container.GetComponentInParent<Ship>() != null;
	}

	// Add container to cache after Valheim finishes constructing it.
	[HarmonyPatch(typeof(Container), "Awake")]
	private static class ContainerAwakePatch
	{
		private static void Postfix(Container __instance)
		{
			loadedContainers.Add(__instance);
		}
	}

	// Remove container after Valheim marks it destroyed.
	[HarmonyPatch(typeof(Container), "OnDestroyed")]
	private static class ContainerDestroyedPatch
	{
		private static void Postfix(Container __instance)
		{
			loadedContainers.Remove(__instance);
		}
	}

	// Stop Valheim from picking up or moving an item during the favorite left-click shortcut.
	[HarmonyPatch(typeof(InventoryGrid), "OnLeftClick")]
	private static class InventoryLeftClickPatch
	{
		private static bool Prefix()
		{
			return !IsFavoriteMouseActive(0);
		}
	}

	// Valheim completes some item pickup operations when the mouse button releases.
	[HarmonyPatch(typeof(InventoryGrid), "OnLeftRelease")]
	private static class InventoryLeftReleasePatch
	{
		private static bool Prefix()
		{
			return !IsFavoriteMouseActive(0);
		}
	}

	// Valheim starts inventory pickup through InventoryGui.OnSelectedItem.
	[HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
	private static class InventorySelectedItemPatch
	{
		private static bool Prefix()
		{
			return !IsFavoriteMouseActive(0);
		}
	}

	// Stop Valheim from using or consuming an item during the favorite right-click shortcut.
	[HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
	private static class InventoryRightClickPatch
	{
		private static bool Prefix()
		{
			return !IsFavoriteMouseActive(1);
		}
	}

	[HarmonyPatch(typeof(InventoryGui), "Show")]
	private static class InventoryGuiShowPatch
	{
		private static void Postfix(Container container)
		{
			openedContainer = container;
		}
	}

	[HarmonyPatch(typeof(InventoryGui), "Hide")]
	private static class InventoryGuiHidePatch
	{
		private static void Postfix()
		{
			openedContainer = null;
		}
	}

	private static bool IsOpenedByLocalPlayer(Container container)
	{
		return container == openedContainer;
	}

	// True while a favorite shortcut that uses this mouse button (0 = left, 1 = right)
	// has its modifiers held. The click patches use it to stop the normal inventory action.
	private static bool IsFavoriteMouseActive(int mouseButton)
	{
		return IsFavoriteShortcutActive(favoriteItemHotkey, mouseButton) ||
			IsFavoriteShortcutActive(favoriteSlotHotkey, mouseButton);
	}

	private static bool IsFavoriteShortcutActive(ConfigEntry<KeyboardShortcut>? entry, int mouseButton)
	{
		if (entry == null) return false;

		KeyboardShortcut shortcut = entry.Value;
		if (shortcut.MainKey != KeyCode.Mouse0 + mouseButton) return false;

		foreach (KeyCode modifier in shortcut.Modifiers)
		{
			if (!IsModifierHeld(modifier)) return false;
		}

		return true;
	}
}

// This class reads a gamepad "hold modifier + press button" shortcut.
// It uses Unity's Input System (the layer the game's own input sits on).
// Types are accessed by reflection, so the mod needs no reference to Unity.InputSystem.dll.
internal sealed class GamepadCombo
{
	// These names can be used in the config. They map to Input System gamepad controls.
	internal const string ValidNames =
		"BumperL, BumperR, TriggerL, TriggerR, StickL, StickR, " +
		"DPadLeft, DPadRight, DPadUp, DPadDown, " +
		"ButtonNorth (Y), ButtonSouth (A), ButtonWest (X), ButtonEast (B), " +
		"Start, Select";

	// Config name -> Input System control path on Gamepad.
	private static readonly Dictionary<string, string> Aliases =
		new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
		{
			{ "BumperL", "leftShoulder" },
			{ "LeftShoulder", "leftShoulder" },
			{ "BumperR", "rightShoulder" },
			{ "RightShoulder", "rightShoulder" },
			{ "TriggerL", "leftTrigger" },
			{ "LeftTrigger", "leftTrigger" },
			{ "TriggerR", "rightTrigger" },
			{ "RightTrigger", "rightTrigger" },
			{ "StickL", "leftStickButton" },
			{ "StickR", "rightStickButton" },
			{ "DPadLeft", "dpad.left" },
			{ "DPadRight", "dpad.right" },
			{ "DPadUp", "dpad.up" },
			{ "DPadDown", "dpad.down" },
			{ "ButtonNorth", "buttonNorth" },
			{ "ButtonY", "buttonNorth" },
			{ "ButtonSouth", "buttonSouth" },
			{ "ButtonA", "buttonSouth" },
			{ "ButtonWest", "buttonWest" },
			{ "ButtonX", "buttonWest" },
			{ "ButtonEast", "buttonEast" },
			{ "ButtonB", "buttonEast" },
			{ "Start", "startButton" },
			{ "Select", "selectButton" }
		};

	// The Input System lookup is shared by every combo.
	private static PropertyInfo? currentProperty;
	private static int lookupAttempts;
	private static bool gaveUp;

	private readonly ConfigEntry<string> modifierEntry;
	private readonly ConfigEntry<string> buttonEntry;
	private readonly BepInEx.Logging.ManualLogSource log;

	private string? cachedModifier;
	private string? cachedButton;
	private string? modifierPath;
	private string? buttonPath;

	internal GamepadCombo(
		ConfigEntry<string> modifier,
		ConfigEntry<string> button,
		BepInEx.Logging.ManualLogSource logger)
	{
		modifierEntry = modifier;
		buttonEntry = button;
		log = logger;
	}

	// Returns true only on the frame when the button is pressed while the modifier is held.
	internal bool WasPressed()
	{
		if (!EnsureInputSystem())
		{
			return false;
		}

		object? pad = currentProperty!.GetValue(null, null);

		// No gamepad connected.
		if (pad == null)
		{
			return false;
		}

		Refresh(pad);

		if (buttonPath == null)
		{
			return false;
		}

		if (modifierPath != null)
		{
			object? modifier = GetControl(pad, modifierPath);
			if (modifier == null || !ReadBool(modifier, "isPressed"))
			{
				return false;
			}
		}

		object? button = GetControl(pad, buttonPath);
		return button != null && ReadBool(button, "wasPressedThisFrame");
	}

	// Re-resolves and validates the configured names when the config values change.
	private void Refresh(object pad)
	{
		if (modifierEntry.Value == cachedModifier && buttonEntry.Value == cachedButton)
		{
			return;
		}

		cachedModifier = modifierEntry.Value;
		cachedButton = buttonEntry.Value;

		modifierPath = ResolveAndValidate(pad, cachedModifier, modifierEntry.Definition.Key);
		buttonPath = ResolveAndValidate(pad, cachedButton, buttonEntry.Definition.Key);
	}

	private string? ResolveAndValidate(object pad, string configured, string settingName)
	{
		string name = (configured ?? string.Empty).Trim();

		if (name.Length == 0 ||
			name.Equals("None", System.StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		string path = Aliases.TryGetValue(name, out string? alias) ? alias : name;

		if (GetControl(pad, path) == null)
		{
			log.LogError(
				$"Unknown gamepad button '{configured}' for '{settingName}'. " +
				$"Valid values: {ValidNames}");
			return null;
		}

		return path;
	}

	private static object? GetControl(object pad, string path)
	{
		object? current = pad;

		foreach (string part in path.Split('.'))
		{
			PropertyInfo? property = current.GetType().GetProperty(
				part,
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

			if (property == null)
			{
				return null;
			}

			current = property.GetValue(current, null);

			if (current == null)
			{
				return null;
			}
		}

		return current;
	}

	internal static bool ReadBool(object control, string member)
	{
		PropertyInfo? property = control.GetType().GetProperty(
			member,
			BindingFlags.Public | BindingFlags.Instance);

		return property != null &&
			property.GetValue(control, null) is bool value &&
			value;
	}

	private bool EnsureInputSystem()
	{
		if (currentProperty != null)
		{
			return true;
		}

		if (gaveUp)
		{
			return false;
		}

		System.Type? gamepadType = null;

		try
		{
			gamepadType = System.Type.GetType(
				"UnityEngine.InputSystem.Gamepad, Unity.InputSystem",
				false);

			if (gamepadType == null)
			{
				foreach (Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
				{
					gamepadType = assembly.GetType("UnityEngine.InputSystem.Gamepad", false);

					if (gamepadType != null)
					{
						break;
					}
				}
			}
		}
		catch (System.Exception)
		{
			// Try again next frame.
		}

		currentProperty = gamepadType?.GetProperty(
			"current",
			BindingFlags.Public | BindingFlags.Static);

		if (currentProperty != null)
		{
			return true;
		}

		// The assembly may not be loaded yet right after startup.
		if (++lookupAttempts >= 300)
		{
			gaveUp = true;
			log.LogError("Unity Input System (Gamepad) not found. Gamepad shortcut disabled.");
		}

		return false;
	}
}

// This class reads the keyboard through Unity's Input System (Keyboard.current).
// It complements Unity's legacy input so shortcuts keep working after switching between
// keyboard and gamepad. Types are accessed by reflection, so the mod needs no reference
// to Unity.InputSystem.dll. Mouse buttons are not handled here (legacy input only).
internal static class KeyboardHotkey
{
	internal static BepInEx.Logging.ManualLogSource? Log;

	private static System.Type? keyEnumType;
	private static PropertyInfo? keyboardCurrentProperty;
	private static PropertyInfo? keyboardIndexer;
	private static int lookupAttempts;
	private static bool gaveUp;

	// KeyCode -> Input System Key value (null when there is no match). Avoids re-parsing every frame.
	private static readonly Dictionary<KeyCode, object?> keyCache = new Dictionary<KeyCode, object?>();

	internal static bool WasPressedThisFrame(KeyCode code)
	{
		return ReadKey(code, "wasPressedThisFrame");
	}

	internal static bool IsHeld(KeyCode code)
	{
		return ReadKey(code, "isPressed");
	}

	private static bool ReadKey(KeyCode code, string member)
	{
		// None and mouse/joystick codes have no keyboard key.
		if (code == KeyCode.None || code >= KeyCode.Mouse0 || !EnsureKeyboard())
		{
			return false;
		}

		object? keyboard = keyboardCurrentProperty!.GetValue(null, null);
		if (keyboard == null)
		{
			return false;
		}

		object? key = GetKey(keyboard, code);
		return key != null && GamepadCombo.ReadBool(key, member);
	}

	// Converts a Unity KeyCode into the matching Input System Key name.
	private static string ToKeyName(KeyCode code)
	{
		string name = code.ToString();

		if (name.StartsWith("Alpha")) return "Digit" + name.Substring(5);
		if (name.StartsWith("Keypad")) return "Numpad" + name.Substring(6);

		switch (code)
		{
			case KeyCode.LeftControl: return "LeftCtrl";
			case KeyCode.RightControl: return "RightCtrl";
			case KeyCode.Return: return "Enter";
			case KeyCode.Print: return "PrintScreen";
			case KeyCode.Menu: return "ContextMenu";
			case KeyCode.LeftApple:
			case KeyCode.LeftWindows: return "LeftMeta";
			case KeyCode.RightApple:
			case KeyCode.RightWindows: return "RightMeta";
			default: return name;
		}
	}

	private static object? GetKey(object keyboard, KeyCode code)
	{
		if (keyEnumType == null || keyboardIndexer == null)
		{
			return null;
		}

		if (!keyCache.TryGetValue(code, out object? key))
		{
			try
			{
				// Ignore case: Unity says BackQuote, the Input System says Backquote.
				key = System.Enum.Parse(keyEnumType, ToKeyName(code), true);
			}
			catch (System.Exception)
			{
				// Keys the Input System does not know are ignored.
				key = null;
			}

			keyCache[code] = key;
		}

		if (key == null)
		{
			return null;
		}

		try
		{
			return keyboardIndexer.GetValue(keyboard, new object[] { key });
		}
		catch (System.Exception)
		{
			return null;
		}
	}

	private static bool EnsureKeyboard()
	{
		if (keyboardCurrentProperty != null && keyboardIndexer != null)
		{
			return true;
		}

		if (gaveUp)
		{
			return false;
		}

		System.Type? keyboardType = FindInputSystemType("UnityEngine.InputSystem.Keyboard");
		keyEnumType = FindInputSystemType("UnityEngine.InputSystem.Key");

		if (keyboardType != null && keyEnumType != null)
		{
			keyboardCurrentProperty = keyboardType.GetProperty(
				"current",
				BindingFlags.Public | BindingFlags.Static);

			keyboardIndexer = keyboardType.GetProperty(
				"Item",
				new System.Type[] { keyEnumType });
		}

		if (keyboardCurrentProperty != null && keyboardIndexer != null)
		{
			return true;
		}

		// The assembly may not be loaded yet right after startup.
		if (++lookupAttempts >= 300)
		{
			gaveUp = true;
			Log?.LogWarning("Input System keyboard not found. Using legacy keyboard input only.");
		}

		return false;
	}

	private static System.Type? FindInputSystemType(string fullName)
	{
		try
		{
			System.Type? type = System.Type.GetType(fullName + ", Unity.InputSystem", false);
			if (type != null)
			{
				return type;
			}

			foreach (Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
			{
				type = assembly.GetType(fullName, false);
				if (type != null)
				{
					return type;
				}
			}
		}
		catch (System.Exception)
		{
			// Try again next frame.
		}

		return null;
	}
}
