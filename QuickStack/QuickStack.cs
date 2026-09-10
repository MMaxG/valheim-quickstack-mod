using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// This plugin is the entry point for QuickStack.
// BepInEx creates this class when Valheim loads the mod.
[BepInPlugin("quickstack", "QuickStack", "0.1.0")]
public sealed class QuickStackPlugin : BaseUnityPlugin
{
	// This cache stores containers currently loaded by this game client.
	// A HashSet prevents duplicate entries and supports fast removal.
	private static readonly HashSet<Container> loadedContainers = new HashSet<Container>();

	// This prototype stores favorite inventory slots by grid position.
	private readonly HashSet<Vector2i> favoriteSlots = new HashSet<Vector2i>();

	// This set stores item identities so protection follows items between slots.
	private readonly HashSet<string> favoriteItemIdentities = new HashSet<string>();

	// This config entry stores favorite positions across game restarts.
	private ConfigEntry<string> favoriteSlotConfig = null!;

	// This config entry stores favorite item identities across game restarts.
	private ConfigEntry<string> favoriteItemConfig = null!;

	// This setting lets players change the hotkey without rebuilding the mod.
	private ConfigEntry<KeyboardShortcut> quickStackHotkey = null!;

	// This setting controls how far QuickStack searches for containers.
	private ConfigEntry<float> quickStackRange = null!;

	// These settings protect commonly used player items from automatic transfer.
	private ConfigEntry<bool> skipEquippedItems = null!;
	private ConfigEntry<bool> skipHotbarItems = null!;

	// Awake runs once when BepInEx creates the plugin.
	// Use it for setup that should happen one time.
	private void Awake()
	{
		// Store the hotkey in BepInEx configuration.
		// F8 becomes the default only when no saved setting exists yet.
		quickStackHotkey = Config.Bind(
			"General",
			"Quick stack hotkey",
			new KeyboardShortcut(KeyCode.BackQuote),
			"Press this key to quick-stack items.");

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
		LoadFavoriteSlots();
		LoadFavoriteItems();

		// Harmony lets this plugin add small methods before or after Valheim methods.
		new Harmony("quickstack").PatchAll();
		// Persist newly added settings immediately so users can edit the config file.
		Config.Save();

		// This confirms that BepInEx loaded the plugin successfully.
		Logger.LogInfo("QuickStack loaded. Press backtick to quick-stack.");
	}

	// Update runs once every rendered frame while the game is running.
	// Keep this check small because it runs many times per second.
	private void Update()
	{
		// IsDown returns true only on the frame when the player presses the key.
		if (quickStackHotkey.Value.IsDown())
		{
			// Keep input detection separate from inventory behavior.
			RunQuickStack();
		}

		// Mouse input needs direct Unity checks; KeyboardShortcut handles keyboard keys only.
		if ((Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) && Input.GetMouseButtonDown(0))
		{
			HandleFavoriteClick(false);
		}

		if ((Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) && Input.GetMouseButtonDown(1))
		{
			HandleFavoriteClick(true);
		}

		// Inventory UI can recreate slot elements after loading or refreshes.
		// Reapply missing borders while the player inventory is visible.
		ApplyFavoriteBorders();
	}

	// This method toggles item or slot protection based on the Alt-click button used.
	private void HandleFavoriteClick(bool slotMode)
	{
		if (InventoryGui.instance == null || InventoryGui.instance.m_playerGrid == null)
		{
			Logger.LogInfo("Player inventory is not open.");
			return;
		}

		// get_SelectionGridPosition reports gamepad selection, not mouse position.
		// Check each visible inventory element against the current mouse position instead.
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
			RectTransform elementRect = element.GetComponent<RectTransform>();
			if (RectTransformUtility.RectangleContainsScreenPoint(
				elementRect,
				Input.mousePosition,
				null))
			{
				Vector2i position = (Vector2i)positionProperty.GetValue(element, null)!;
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
		}

		Logger.LogInfo("Mouse is not over a player inventory slot.");
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

	// This method creates four thin UI images instead of covering the item with a solid color.
	private void AddFavoriteBorder(InventoryElement element, bool identityMode = false)
	{
		Transform existingBorder = element.transform.Find("QuickStackFavoriteBorder");
		ColorUtility.TryParseHtmlString(identityMode ? "#1C92D8B3" : "#E2A965B3", out Color borderColor);
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
			if (distance <= quickStackRange.Value)
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
		else
		{
			Logger.LogInfo($"QuickStack moved {movedItemCount} item(s).");
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
			ItemDrop.ItemData? matchingContainerItem = null;
			List<ItemDrop.ItemData> containerItems = inventory.GetAllItems();
			foreach (ItemDrop.ItemData containerItem in containerItems)
			{
				if (containerItem.m_shared.m_name == playerItem.m_shared.m_name)
				{
					matchingContainerItem = containerItem;
					break;
				}
			}

			if (matchingContainerItem == null)
			{
				continue;
			}

			// Calculate capacity directly because FindFreeStackSpace can report zero
			// for an item that has no matching stack, not only for a full chest.
			int availableSpace = GetAvailableSpace(inventory, playerItem);
			int amountToMove = availableSpace;
			amountToMove = Mathf.Min(amountToMove, playerItem.m_stack);
			if (amountToMove <= 0)
			{
				continue;
			}

			string itemName = playerItem.m_shared.m_name;
			// Valheim's transfer method updates source and destination inventories together.
			if (amountToMove == playerItem.m_stack)
			{
				// Full-stack overload chooses a valid destination slot internally.
				inventory.MoveItemToThis(playerInventory, playerItem);
			}
			else
			{
				// Partial overload needs an actual destination grid position.
				Vector2i destination = matchingContainerItem.m_gridPos;
				if (matchingContainerItem.m_stack >= matchingContainerItem.m_shared.m_maxStackSize)
				{
					if (!TryFindEmptySlot(inventory, out destination))
					{
						continue;
					}
				}

				inventory.MoveItemToThis(playerInventory, playerItem, amountToMove, destination.x, destination.y);
			}
			movedItemCount += amountToMove;
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
}
