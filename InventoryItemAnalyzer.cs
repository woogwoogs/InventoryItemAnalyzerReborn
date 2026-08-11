using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.FilesInMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Nodes;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

using Color = SharpDX.Color;

namespace InventoryItemAnalyzer;

public partial class InventoryItemAnalyzer : BaseSettingsPlugin<Settings>
{
    private HoverItemIcon _hoverItemIcon;
    private Entity _lastHoverItem;
    private SharpDX.RectangleF _lastHoverRect;

    // Analysis is expensive (modifier/stat parsing). Cache results by item
    // address so Render() only does the expensive work when an item/settings
    // actually changes.
    private readonly Dictionary<long, CachedItemAnalysis> _analysisCache = new();
    private long _analysisSettingsSignature;

    private sealed class CachedItemAnalysis
    {
        public int Rating;
        public List<string> CompactRows;
        public List<string> FullRows;
    }

    public override bool Initialise()
    {
        _analysisSettingsSignature = ComputeAnalysisSettingsSignature();
        _analysisCache.Clear();
        return true;
    }

    public override Job Tick()
    {
        if (!Initialized)
            return null;

        // Settings are checked once per plugin tick, not once per inventory
        // item. This keeps cache invalidation cheap even with a full inventory.
        RefreshAnalysisCacheIfSettingsChanged();

        var hoverItemIcon = GameController?.Game?.IngameState?.UIHover?.AsObject<HoverItemIcon>();
        if (hoverItemIcon != null && hoverItemIcon.IsValid)
            _hoverItemIcon = hoverItemIcon;

        return null;
    }

    public override void Render()
    {
        if (!Settings.Enable)
            return;

        try
        {
            var inventoryHandled = false;

            // Existing, known-good inventory path.
            var inventoryPanel = GameController.IngameState.IngameUi.InventoryPanel;
            if (inventoryPanel != null && inventoryPanel.IsVisible)
            {
                var sourceItems =
                    GameController.IngameState.ServerData.PlayerInventories[0]
                        .Inventory.InventorySlotItems;

                if (sourceItems != null)
                {
                    foreach (var inventoryItem in sourceItems)
                    {
                        if (inventoryItem?.Item == null)
                            continue;

                        var rect = inventoryItem.GetClientRect();
                        if (rect.Width <= 0 || rect.Height <= 0)
                            continue;

                        // Stars are part of the analyzer's user-defined rating system.
                        // Draw them independently of the item-info hotkey.
                        var starRating = GetCachedRating(inventoryItem.Item);
                        if (starRating > 0)
                            DrawQualityStars(rect, starRating);

                        if (Settings.ShowItemInfo && IsMouseOver(rect) &&
                            IsItemInfoPopupVisible())
                        {
                            DrawItemInfoOverlay(inventoryItem.Item, rect);
                            inventoryHandled = true;
                        }
                    }
                }
            }

            if (inventoryHandled || !Settings.ShowItemInfo)
                return;

            // Equipped items and chat-linked items both surface through
            // ExileCore's UIHover.HoverItemIcon. This is the same API path
            // used by AdvancedTooltipPlus and is much more reliable than
            // walking the UI object graph with reflection.
            var hover = _hoverItemIcon;

            // If Alt/the configured key causes the game's tooltip to rebuild and
            // UIHover briefly disappears, reuse the last valid equipped/chat item
            // for this same key hold.
            if ((hover == null || !hover.IsValid) && IsItemInfoPopupVisible() &&
                _lastHoverItem != null && _lastHoverItem.IsValid &&
                _lastHoverItem.GetComponent<Mods>() != null &&
                IsMouseOver(_lastHoverRect))
            {
                DrawItemInfoOverlay(_lastHoverItem, _lastHoverRect);
                return;
            }

            if (hover == null || !hover.IsValid)
                return;

            var hoverItem = hover.Item;
            var tooltipFrame = hover.ItemFrame;

            // Equipped gear can report ToolTipType.None even though UIHover has
            // a valid item under the cursor. Keep processing the item instead of
            // returning early so equipped gear can use the same analyzer path.

            if (hoverItem == null || hoverItem.Address == 0 ||
                !hoverItem.IsValid || hoverItem.GetComponent<Mods>() == null)
                return;

            // Inventory is already handled above. UIHover handles equipped
            // gear and other item contexts (chat/stash/etc.). Equipped items
            // may not expose a normal ItemFrame, so those use the mouse anchor
            // fallback below.
            if (tooltipFrame == null)
            {
                if (IsItemInfoPopupVisible())
                {
                    var mouse = ImGuiNET.ImGui.GetIO().MousePos;
                    var mouseRect = new SharpDX.RectangleF(mouse.X, mouse.Y, 1f, 1f);

                    _lastHoverItem = hoverItem;
                    _lastHoverRect = mouseRect;

                    var equippedRating = GetCachedRating(hoverItem);
                    if (equippedRating > 0)
                        DrawQualityStars(mouseRect, equippedRating);

                    DrawItemInfoOverlay(hoverItem, mouseRect);
                }
                return;
            }

            var hoverRect = tooltipFrame.GetClientRect();
            var hoverRectFallbackToMouse = false;

            if (hoverRect.Width <= 0 || hoverRect.Height <= 0)
            {
                var mouse = ImGuiNET.ImGui.GetIO().MousePos;
                hoverRect = new SharpDX.RectangleF(mouse.X, mouse.Y, 1f, 1f);
                hoverRectFallbackToMouse = true;
            }

            // UIHover has already established that this item is the item under
            // the cursor. When ExileCore does not expose an ItemFrame (common
            // for equipped gear), use the mouse as the temporary anchor instead
            // of rejecting the item.
            if (!hoverRectFallbackToMouse && !IsMouseOver(hoverRect))
            {
                _lastHoverItem = null;
                return;
            }

            // Keep the last valid item/rect only while the cursor is over it so
            // a transient UIHover rebuild does not cause a one-frame flash.
            _lastHoverItem = hoverItem;
            _lastHoverRect = hoverRect;

            var hoverRating = GetCachedRating(hoverItem);
            if (hoverRating > 0)
                DrawQualityStars(hoverRect, hoverRating);

            if (IsItemInfoPopupVisible())
            {
                DrawItemInfoOverlay(hoverItem, hoverRect);
            }
            else
            {
                _lastHoverItem = null;
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"InventoryItemAnalyzer Render: {ex}");
        }
    }


    // IFL terminology:
    // User-defined star thresholds: 1, 2, and 3 stars.
    //
    // The IFLs are used as the specification. The plugin evaluates the same
    // thresholds directly from the item's explicit mod values.

    private int GetIFLQuality(Entity item)
    {
        var mods = item.GetComponent<Mods>();
        if (mods == null || mods.ItemRarity != ItemRarity.Rare || mods.ItemMods == null)
            return 0;

        var path = item.Path ?? string.Empty;
        var stats = ReadQualityStats(mods);

        // The star system is always user-defined. There is no enable/disable
        // switch and no legacy fallback rating.
        try
        {
            var armour = item.GetComponent<Armour>();
            if (armour != null)
            {
                var finalDefenses = CalculateFinalDefenses(item, mods, armour);
                stats.EnergyShield = finalDefenses.EnergyShield;
            }
        }
        catch
        {
        }

        return EvaluateCustomStars(item, stats, path);
    }

    private int EvaluateCustomStars(Entity item, QualityStats s, string path)
    {
        var slot = GetStarSlot(path);
        if (slot == null)
            return 0;

        var qualifying = CountSlotQualifyingStats(item, s, slot);

        if (qualifying >= slot.ThreeStarRequired) return 3;
        if (qualifying >= slot.TwoStarRequired) return 2;
        if (qualifying >= slot.OneStarRequired) return 1;
        return 0;
    }

    private sealed class StarSlotRules
    {
        public int OneStarRequired;
        public int TwoStarRequired;
        public int ThreeStarRequired;
        public Dictionary<string, int> Thresholds = new Dictionary<string, int>();
    }

    private int CountSlotQualifyingStats(Entity item, QualityStats s, StarSlotRules slot)
    {
        var count = 0;

        foreach (var kv in slot.Thresholds)
        {
            if (kv.Value <= 0) continue;

            var value = 0;
            switch (kv.Key)
            {
                case "Life": value = s.Life; break;
                case "Cold Resistance": value = s.ColdRes + s.AllRes; break;
                case "Fire Resistance": value = s.FireRes + s.AllRes; break;
                case "Lightning Resistance": value = s.LightningRes + s.AllRes; break;
                case "Chaos Resistance": value = s.ChaosRes; break;
                case "Energy Shield": value = s.EnergyShield; break;
                case "Spell Suppression": value = s.SpellSuppression; break;
                case "Attributes": value = Math.Max(s.Strength, Math.Max(s.Dexterity, s.Intelligence)); break;
                case "Attack Speed": value = s.AttackSpeed; break;
                case "Cast Speed": value = s.CastSpeed; break;
                case "Crit Multiplier": value = s.CritMultiplier; break;
                case "Movement Speed": value = s.MoveSpeed; break;
                case "Mana": value = s.Mana; break;
                case "Gem Levels": value = s.GemLevels; break;
                case "Physical DPS":
                    {
                        var mods = item.GetComponent<Mods>();
                        if (mods != null)
                            value = (int)Math.Round(CalculateWeaponDps(item, mods).Physical);
                    }
                    break;

                case "Total Weapon DPS":
                    {
                        var mods = item.GetComponent<Mods>();
                        if (mods != null)
                            value = (int)Math.Round(CalculateWeaponDps(item, mods).Total);
                    }
                    break;
            }

            if (value >= kv.Value) count++;
        }

        return count;
    }

    private StarSlotRules GetStarSlot(string path)
    {
        var p = (path ?? string.Empty).ToLowerInvariant();

        // Match specific equipment classes first. Many PoE armour metadata
        // paths contain the generic word "Armour", so checking that first can
        // incorrectly assign boots/gloves to the Body Armour rules.
        if (p.Contains("helmet") || p.Contains("helm") || p.Contains("circlet")) return BuildHelmetRules();
        if (p.Contains("boot")) return BuildBootsRules();
        if (p.Contains("glove")) return BuildGlovesRules();
        if (p.Contains("belt")) return BuildBeltRules();
        if (p.Contains("bodyarmour") || p.Contains("bodyarmours") ||
            p.Contains("body_armour") || p.Contains("chest"))
            return BuildBodyArmourRules();
        if (p.Contains("ring")) return BuildRingRules();
        if (p.Contains("amulet")) return BuildAmuletRules();
        if (p.Contains("shield") || p.Contains("buckler")) return BuildShieldRules();
        if (p.Contains("quiver")) return BuildQuiverRules();
        if (IsWeaponLike(path)) return BuildWeaponRules();

        return null;
    }

    private StarSlotRules BuildHelmetRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Helmet_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Helmet_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Helmet_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Helmet_Life.Value,
                ["Cold Resistance"] = Settings.Star_Helmet_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Helmet_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Helmet_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Helmet_ChaosResistance.Value,
                ["Energy Shield"] = Settings.Star_Helmet_EnergyShield.Value,
                ["Spell Suppression"] = Settings.Star_Helmet_SpellSuppression.Value,
                ["Attributes"] = Settings.Star_Helmet_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildBodyArmourRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_BodyArmour_GoodRequired.Value,
            TwoStarRequired = Settings.Star_BodyArmour_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_BodyArmour_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_BodyArmour_Life.Value,
                ["Cold Resistance"] = Settings.Star_BodyArmour_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_BodyArmour_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_BodyArmour_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_BodyArmour_ChaosResistance.Value,
                ["Energy Shield"] = Settings.Star_BodyArmour_EnergyShield.Value,
                ["Spell Suppression"] = Settings.Star_BodyArmour_SpellSuppression.Value,
                ["Attributes"] = Settings.Star_BodyArmour_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildGlovesRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Gloves_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Gloves_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Gloves_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Gloves_Life.Value,
                ["Cold Resistance"] = Settings.Star_Gloves_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Gloves_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Gloves_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Gloves_ChaosResistance.Value,
                ["Spell Suppression"] = Settings.Star_Gloves_SpellSuppression.Value,
                ["Attack Speed"] = Settings.Star_Gloves_AttackSpeed.Value,
                ["Cast Speed"] = Settings.Star_Gloves_CastSpeed.Value,
                ["Crit Multiplier"] = Settings.Star_Gloves_CritMultiplier.Value,
                ["Attributes"] = Settings.Star_Gloves_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildBootsRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Boots_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Boots_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Boots_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Boots_Life.Value,
                ["Cold Resistance"] = Settings.Star_Boots_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Boots_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Boots_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Boots_ChaosResistance.Value,
                ["Spell Suppression"] = Settings.Star_Boots_SpellSuppression.Value,
                ["Attributes"] = Settings.Star_Boots_Attributes.Value,
                ["Movement Speed"] = Settings.Star_Boots_MovementSpeed.Value,
            }
        };
    }

    private StarSlotRules BuildBeltRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Belt_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Belt_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Belt_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Belt_Life.Value,
                ["Cold Resistance"] = Settings.Star_Belt_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Belt_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Belt_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Belt_ChaosResistance.Value,
                ["Attributes"] = Settings.Star_Belt_Attributes.Value,
                ["Mana"] = Settings.Star_Belt_Mana.Value,
            }
        };
    }

    private StarSlotRules BuildRingRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Ring_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Ring_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Ring_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Ring_Life.Value,
                ["Cold Resistance"] = Settings.Star_Ring_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Ring_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Ring_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Ring_ChaosResistance.Value,
                ["Attributes"] = Settings.Star_Ring_Attributes.Value,
                ["Mana"] = Settings.Star_Ring_Mana.Value,
                ["Attack Speed"] = Settings.Star_Ring_AttackSpeed.Value,
                ["Cast Speed"] = Settings.Star_Ring_CastSpeed.Value,
                ["Crit Multiplier"] = Settings.Star_Ring_CritMultiplier.Value,
                ["Accuracy"] = Settings.Star_Ring_Accuracy.Value,
            }
        };
    }

    private StarSlotRules BuildAmuletRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Amulet_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Amulet_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Amulet_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Amulet_Life.Value,
                ["Cold Resistance"] = Settings.Star_Amulet_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Amulet_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Amulet_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Amulet_ChaosResistance.Value,
                ["Attributes"] = Settings.Star_Amulet_Attributes.Value,
                ["Energy Shield"] = Settings.Star_Amulet_EnergyShield.Value,
                ["Gem Levels"] = Settings.Star_Amulet_GemLevels.Value,
                ["Crit Multiplier"] = Settings.Star_Amulet_CritMultiplier.Value,
            }
        };
    }

    private StarSlotRules BuildShieldRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Shield_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Shield_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Shield_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Shield_Life.Value,
                ["Cold Resistance"] = Settings.Star_Shield_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Shield_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Shield_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Shield_ChaosResistance.Value,
                ["Energy Shield"] = Settings.Star_Shield_EnergyShield.Value,
                ["Spell Suppression"] = Settings.Star_Shield_SpellSuppression.Value,
                ["Attributes"] = Settings.Star_Shield_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildQuiverRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Quiver_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Quiver_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Quiver_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Quiver_Life.Value,
                ["Cold Resistance"] = Settings.Star_Quiver_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Quiver_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Quiver_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Quiver_ChaosResistance.Value,
                ["Attack Speed"] = Settings.Star_Quiver_AttackSpeed.Value,
                ["Crit Multiplier"] = Settings.Star_Quiver_CritMultiplier.Value,
                ["Attributes"] = Settings.Star_Quiver_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildWeaponRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Weapon_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Weapon_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Weapon_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                [Settings.Star_Weapon_DpsMode.Value == 0 ? "Physical DPS" : "Total Weapon DPS"] =
                    Settings.Star_Weapon_WeaponDps.Value,
                ["Attack Speed"] = Settings.Star_Weapon_AttackSpeed.Value,
                ["Crit Multiplier"] = Settings.Star_Weapon_CritMultiplier.Value,
                ["Cold Resistance"] = Settings.Star_Weapon_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Weapon_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Weapon_LightningResistance.Value,
                ["Attributes"] = Settings.Star_Weapon_Attributes.Value,
                ["Gem Levels"] = Settings.Star_Weapon_GemLevels.Value,
            }
        };
    }

    private int EvaluateBoots(QualityStats s)
    {
        // Excellent:
        // 30 MS + 90 Life + 2 strong res
        // OR 35 MS + 80 Life + 2 strong res
        if ((s.MoveSpeed >= 30 && StrongPoolCount(s) >= 2) ||
            (s.MoveSpeed >= 35 && StrongPoolCount(s) >= 2))
            return 3;

        // Great: 30 MS + 2 medium qualifying stats
        if (s.MoveSpeed >= 30 && MediumPoolCount(s) >= 2)
            return 2;

        // Good: 25 MS + 1 qualifying stat
        if (s.MoveSpeed >= 25 && BasicPoolCount(s) >= 1)
            return 1;

        return 0;
    }

    private int EvaluateGloves(QualityStats s)
    {
        if (StrongPoolCount(s) >= 3)
            return 3;

        if (MediumPoolCount(s) >= 2)
            return 2;

        if (BasicPoolCount(s) >= 2)
            return 1;

        return 0;
    }

    private int EvaluateHelmet(QualityStats s)
    {
        if (StrongPoolCount(s) >= 3)
            return 3;

        if (MediumPoolCount(s) >= 2)
            return 2;

        if (BasicPoolCount(s) >= 2)
            return 1;

        return 0;
    }

    private int EvaluateBodyArmour(QualityStats s)
    {
        if (StrongPoolCount(s) >= 3)
            return 3;

        if (MediumPoolCount(s) >= 2)
            return 2;

        if (BasicPoolCount(s) >= 2)
            return 1;

        return 0;
    }

    private int EvaluateJewellery(QualityStats s)
    {
        if (CountTrue(
                s.Life >= 90,
                s.FireRes >= 40,
                s.ColdRes >= 40,
                s.LightningRes >= 40,
                s.ChaosRes >= 30,
                s.AllRes >= 12,
                s.Strength >= 35) >= 3)
            return 3;

        if (CountTrue(
                s.Life >= 80,
                s.FireRes >= 35,
                s.ColdRes >= 35,
                s.LightningRes >= 35,
                s.ChaosRes >= 25,
                s.AllRes >= 12,
                s.Strength >= 30) >= 2)
            return 2;

        if (CountTrue(
                s.Life >= 60,
                s.FireRes >= 30,
                s.ColdRes >= 30,
                s.LightningRes >= 30,
                s.ChaosRes >= 20,
                s.AllRes >= 10,
                s.Strength >= 25) >= 2)
            return 1;

        return 0;
    }

    private int EvaluateTwoHandAxe(Mods mods)
    {
        var addedPhys = HasMod(mods, "AddedPhysicalDamage");
        var increasedPhys = HasMod(mods, "IncreasedPhysicalDamagePercent");
        var localPhys = HasMod(mods, "LocalIncreasedPhysicalDamagePercentAndAccuracyRating");
        var attackSpeed = HasMod(mods, "LocalIncreasedAttackSpeed");

        var core = CountTrue(addedPhys, increasedPhys, localPhys);

        // Excellent: all four.
        if (core >= 3 && attackSpeed)
            return 3;

        // Great: 3 core OR 2 core + attack speed.
        if (core >= 3 || (core >= 2 && attackSpeed))
            return 2;

        // Good: 2 core.
        if (core >= 2)
            return 1;

        return 0;
    }

    private sealed class QualityStats
    {
        public int Life;
        public int FireRes;
        public int ColdRes;
        public int LightningRes;
        public int ChaosRes;
        public int AllRes;
        public int MoveSpeed;
        public int Strength;
        public int Dexterity;
        public int Intelligence;
        public int IncreasedLife;
        public int SpecificGemLevels;
        public int SpellSuppression;
        public int AttackSpeed;
        public int CastSpeed;
        public int CritMultiplier;
        public int CritChance;
        public int Accuracy;
        public int Mana;
        public int CooldownRecovery;
        public int LifeRegeneration;
        public int DotMultiplier;
        public int GemLevels;
        public int EnergyShield;
        public int IncreasedEnergyShield;
        public int EsRechargeRate;
        public int FasterEsRechargeStart;
    }

    private static QualityStats ReadQualityStats(Mods mods)
    {
        var s = new QualityStats();

        if (mods?.ItemMods == null)
            return s;

        foreach (var mod in mods.ItemMods)
        {
            if (mod == null)
                continue;

            // Different ExileCore builds expose the useful stat identity in
            // slightly different members. Use all of the stable identifiers
            // instead of relying on RawName alone.
            var key = string.Join(" ", new[]
            {
                GetMemberString(mod, "RawName"),
                GetMemberString(mod, "Name"),
                GetMemberString(mod, "DisplayName"),
                GetMemberString(mod, "Group")
            });

            var values = GetMemberValues(mod);
            var value = FirstModValue(mod);

            // The user's runtime can expose the stat identity in a form that
            // does not contain the obvious RawName token. Build a second,
            // human-readable classification string from the same translation
            // used by the overlay. This fixes cases such as "+56 to Maximum Life"
            // where the visible mod is correct but the raw key is opaque.
            var readable = GetReadableModText(mod, GetMemberString(mod, "RawName"), values);
            var statKey = key + " " + readable;

            if (value == 0 && values.Count > 0)
                value = ParseInt(values[0]);

            // Multi-stat mods can contain several values in one ItemMod.
            // FirstModValue() returns the first value, which is wrong for a
            // later clause such as "+26 to Armour +22 to Maximum Life".
            if ((Contains(statKey, "MaximumLife") ||
                 Contains(statKey, "MaxLife") ||
                 Contains(statKey, "Maximum Life")) &&
                readable.IndexOf("Maximum Life", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var lifeMatch = System.Text.RegularExpressions.Regex.Match(
                    readable,
                    @"([+-]?\d+)\s+to\s+Maximum\s+Life",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (lifeMatch.Success)
                    value = ParseInt(lifeMatch.Groups[1].Value);
            }

            if (Contains(statKey, "MaximumLife") || Contains(statKey, "MaxLife") ||
                Contains(statKey, "Maximum Life"))
                s.Life += value;

            if (Contains(statKey, "Increased Maximum Life") ||
                Contains(statKey, "Increased Maximum Life"))
                s.IncreasedLife += value;

            if (Contains(statKey, "Dexterity") || Contains(statKey, "Dex"))
                s.Dexterity += value;

            if (Contains(statKey, "Intelligence") || Contains(statKey, "Int"))
                s.Intelligence += value;

            if (Contains(statKey, "FireDamageResistancePct") || Contains(statKey, "FireResist") ||
                Contains(statKey, "Fire Resistance"))
                s.FireRes += value;

            if (Contains(statKey, "ColdDamageResistancePct") || Contains(statKey, "ColdResist") ||
                Contains(statKey, "Cold Resistance"))
                s.ColdRes += value;

            if (Contains(statKey, "LightningDamageResistancePct") || Contains(statKey, "LightningResist") ||
                Contains(statKey, "Lightning Resistance"))
                s.LightningRes += value;

            if (Contains(statKey, "ChaosDamageResistancePct") || Contains(statKey, "ChaosResist") ||
                Contains(statKey, "Chaos Resistance"))
                s.ChaosRes += value;

            if (Contains(statKey, "ResistAllElementsPct") || Contains(statKey, "AllResist") ||
                Contains(statKey, "All Elemental Resistances"))
                s.AllRes += value;

            if (Contains(statKey, "MovementVelocityPct") || Contains(statKey, "MovementSpeed"))
                s.MoveSpeed += value;

            if (Contains(statKey, "Strength"))
                s.Strength += value;

            // Some PoE attribute mods are exposed as combined text such as
            // "Strength and Dexterity" or "all Attributes". Make sure the
            // user-defined Attributes threshold can see those mods too.
            if (Contains(statKey, "all Attributes") || Contains(statKey, "All Attributes"))
            {
                s.Strength += value;
                s.Dexterity += value;
                s.Intelligence += value;
            }
            else if (Contains(statKey, "Strength and Dexterity") ||
                     Contains(statKey, "Strength & Dexterity"))
            {
                s.Strength += value;
                s.Dexterity += value;
            }
            else if (Contains(statKey, "Strength and Intelligence") ||
                     Contains(statKey, "Strength & Intelligence"))
            {
                s.Strength += value;
                s.Intelligence += value;
            }
            else if (Contains(statKey, "Dexterity and Intelligence") ||
                     Contains(statKey, "Dexterity & Intelligence"))
            {
                s.Dexterity += value;
                s.Intelligence += value;
            }

            if (Contains(statKey, "SpellSuppression") ||
                Contains(statKey, "Spell Suppression"))
                s.SpellSuppression += value;

            if (Contains(statKey, "AttackSpeed") ||
                Contains(statKey, "Attack Speed"))
                s.AttackSpeed += value;

            if (Contains(statKey, "CastSpeed") ||
                Contains(statKey, "Cast Speed"))
                s.CastSpeed += value;

            if (Contains(statKey, "CriticalStrikeMultiplier") ||
                Contains(statKey, "Critical Strike Multiplier") ||
                Contains(statKey, "CritMultiplier"))
                s.CritMultiplier += value;

            if (Contains(statKey, "CriticalStrikeChance") ||
                Contains(statKey, "Critical Strike Chance") ||
                Contains(statKey, "CritChance"))
                s.CritChance += value;

            if (Contains(statKey, "AccuracyRating") ||
                Contains(statKey, "Accuracy Rating"))
                s.Accuracy += value;

            if (Contains(statKey, "Mana"))
                s.Mana += value;

            if (Contains(statKey, "CooldownRecovery") ||
                Contains(statKey, "Cooldown Recovery"))
                s.CooldownRecovery += value;

            if (Contains(statKey, "LifeRegeneration") ||
                Contains(statKey, "Life Regeneration"))
                s.LifeRegeneration += value;

            if (Contains(statKey, "DamageOverTimeMultiplier") ||
                Contains(statKey, "Damage over Time Multiplier") ||
                Contains(statKey, "DamageOverTime"))
                s.DotMultiplier += value;

            if (Contains(statKey, "GemLevel") ||
                Contains(statKey, "Gem Level"))
            {
                s.GemLevels += value;

                // Specific gem-level modifiers contain a skill/gem name in the
                // readable text. Keep a separate counter so generic +1 to all
                // skill gems and +1 to a specific gem can be configured
                // independently.
                if (readable.IndexOf("to Level of", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    readable.IndexOf("to Level of", StringComparison.OrdinalIgnoreCase) >= 0)
                    s.SpecificGemLevels += value;
            }


            if (Contains(statKey, "EnergyShield") ||
                Contains(statKey, "Energy Shield") ||
                Contains(statKey, "maximum Energy Shield") ||
                Contains(statKey, "maximum Energy"))
            {
                if (Contains(statKey, "Recharge") || Contains(statKey, "Recharge Rate"))
                    s.EsRechargeRate += value;
                else if (Contains(statKey, "Faster") || Contains(statKey, "Start"))
                    s.FasterEsRechargeStart += value;
                else if (Contains(statKey, "Increased") ||
                         Contains(statKey, "increased Energy Shield") ||
                         Contains(statKey, "% increased Energy Shield") ||
                         Contains(statKey, "+%"))
                    s.IncreasedEnergyShield += value;
                else
                    s.EnergyShield += value;
            }

            if (Contains(statKey, "EnergyShieldRechargeRate") ||
                Contains(statKey, "Energy Shield Recharge Rate"))
                s.EsRechargeRate += value;

            if (Contains(statKey, "FasterStartOfEnergyShieldRecharge") ||
                Contains(statKey, "Faster Start of Energy Shield Recharge"))
                s.FasterEsRechargeStart += value;
        }

        return s;
    }

    private static int FirstModValue(object mod)
    {
        try
        {
            var values = GetMemberValues(mod);
            if (values.Count > 0)
            {
                var parsed = ParseInt(values[0]);
                if (parsed != 0)
                    return parsed;
            }

            var direct = GetMemberObject(mod, "Value");
            if (direct != null)
            {
                var parsed = ParseInt(direct.ToString());
                if (parsed != 0)
                    return parsed;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static int ParseInt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var match = System.Text.RegularExpressions.Regex.Match(text, @"-?\d+");
        return match.Success && int.TryParse(match.Value, out var value) ? value : 0;
    }

    private static bool HasMod(Mods mods, string text)
    {
        foreach (var mod in mods.ItemMods)
        {
            if (mod != null &&
                (mod.RawName ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static bool Contains(string value, string search)
    {
        return value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int BasicResCount(QualityStats s)
    {
        return CountTrue(
            s.FireRes >= 30,
            s.ColdRes >= 30,
            s.LightningRes >= 30,
            s.ChaosRes >= 20,
            s.AllRes >= 10);
    }

    private static int MediumResCount(QualityStats s)
    {
        return CountTrue(
            s.FireRes >= 35,
            s.ColdRes >= 35,
            s.LightningRes >= 35,
            s.ChaosRes >= 25,
            s.AllRes >= 12);
    }

    private static int StrongResCount(QualityStats s)
    {
        return CountTrue(
            s.FireRes >= 40,
            s.ColdRes >= 40,
            s.LightningRes >= 40,
            s.ChaosRes >= 30,
            s.AllRes >= 12);
    }

    private static int BasicPoolCount(QualityStats s)
    {
        return CountTrue(
            s.Life >= 60,
            s.FireRes >= 30,
            s.ColdRes >= 30,
            s.LightningRes >= 30,
            s.ChaosRes >= 20,
            s.AllRes >= 10);
    }

    private static int MediumPoolCount(QualityStats s)
    {
        return CountTrue(
            s.Life >= 80,
            s.FireRes >= 35,
            s.ColdRes >= 35,
            s.LightningRes >= 35,
            s.ChaosRes >= 25,
            s.AllRes >= 12);
    }

    private static int StrongPoolCount(QualityStats s)
    {
        return CountTrue(
            s.Life >= 90,
            s.FireRes >= 40,
            s.ColdRes >= 40,
            s.LightningRes >= 40,
            s.ChaosRes >= 30,
            s.AllRes >= 12);
    }

    private static int CountTrue(params bool[] values)
    {
        var count = 0;
        foreach (var value in values)
            if (value)
                count++;

        return count;
    }

    private static bool IsBoot(string path)
    {
        return path.IndexOf("/Boots/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/Boot/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsGloves(string path)
    {
        return path.IndexOf("/Gloves/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsHelmet(string path)
    {
        return path.IndexOf("/Helmets/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/Helmet/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsBodyArmour(string path)
    {
        return path.IndexOf("/BodyArmours/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/BodyArmour/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsJewellery(string path)
    {
        return path.IndexOf("/Rings/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/Amulets/", StringComparison.OrdinalIgnoreCase) >= 0;
    }


    private static bool IsWeaponLike(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var p = path.ToLowerInvariant();

        return p.Contains("/weapons/") ||
               p.Contains("/onehand") ||
               p.Contains("/twohand") ||
               p.Contains("/bows/") ||
               p.Contains("/staves/") ||
               p.Contains("/wands/") ||
               p.Contains("/sceptres/") ||
               p.Contains("/swords/") ||
               p.Contains("/axes/") ||
               p.Contains("/maces/") ||
               p.Contains("/claws/") ||
               p.Contains("/daggers/") ||
               p.Contains("/quarterstaves/");
    }

    private static bool IsTwoHandAxe(string path)
    {
        return path.IndexOf("/TwoHandAxes/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/TwoHandedAxes/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void DrawQualityStars(SharpDX.RectangleF rect, int rating)
    {
        var drawList = ImGuiNET.ImGui.GetForegroundDrawList();
        var c = Settings.StarColor.Value;
        var color = (uint)(c.R | (c.G << 8) | (c.B << 16) | (c.A << 24));

        const float gap = 2f;
        var size = Math.Min(16f, Math.Max(8f, rect.Height - 4f));
        var total = rating * size + (rating - 1) * gap;
        var start = rect.Left + (rect.Width - total) / 2f;
        var cy = rect.Top + rect.Height / 2f;

        for (var i = 0; i < rating; i++)
            DrawFilledStar(drawList, start + size / 2f + i * (size + gap), cy, size, color);
    }

    private static void DrawFilledStar(
        ImGuiNET.ImDrawListPtr list,
        float cx,
        float cy,
        float size,
        uint color)
    {
        var outer = size / 2f;
        var inner = outer * .45f;
        var points = new System.Numerics.Vector2[10];

        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? outer : inner;
            var angle = -MathF.PI / 2f + i * MathF.PI / 5f;

            points[i] = new System.Numerics.Vector2(
                cx + MathF.Cos(angle) * radius,
                cy + MathF.Sin(angle) * radius);
        }

        var center = new System.Numerics.Vector2(cx, cy);

        for (var i = 0; i < 10; i++)
        {
            var next = (i + 1) % 10;
            list.AddTriangleFilled(center, points[i], points[next], color);
        }
    }


    private bool IsMouseOver(SharpDX.RectangleF rect)
    {
        var mouse = ImGuiNET.ImGui.GetIO().MousePos;
        return mouse.X >= rect.Left && mouse.X <= rect.Right &&
               mouse.Y >= rect.Top && mouse.Y <= rect.Bottom;
    }

    private List<string> BuildCompactInfoRows(Entity item, Mods mods)
    {
        var rows = new List<string>();
        var rating = mods.ItemRarity == ItemRarity.Rare ? GetIFLQuality(item) : 0;

        // Compact mode is deliberately rating-only. Items with no stars
        // contribute no content to the compact popup.
        if (rating <= 0)
            return rows;

        var details = new List<string>();
        foreach (var detail in BuildUserDefinedRatingDetails(item, rating))
        {
            if (!string.IsNullOrWhiteSpace(detail))
                details.Add(detail);
        }

        if (details.Count == 0)
            return rows;

        rows.Add($"LOOT RATING|{rating}");
        rows.Add("DETAIL|QUALIFYING STATS");

        foreach (var detail in details)
            rows.Add($"DETAIL|{detail}");

        return rows;
    }

    private bool IsFullAnalyzerHotkeyHeld()
    {
        var key = (int)Settings.ItemInfoHotkey.Value;
        if (key == 0)
            return false;

        return (GetAsyncKeyState(key & 0xFF) & 0x8000) != 0;
    }

    private bool IsCompactAnalyzerModeActive()
    {
        // Compact mode is the normal/default view. Holding the configured
        // analyzer hotkey temporarily reveals the complete non-compact view.
        return Settings.ItemInfoCompactMode.Value && !IsFullAnalyzerHotkeyHeld();
    }

    private List<string> BuildNormalInfoRows(Entity item, Mods mods)
    {
        var cached = GetCachedAnalysis(item, mods);

        // Compact mode is the lightweight default and can safely use cached
        // rows. The full analyzer is deliberately rebuilt only when the user
        // explicitly asks for it, preserving the original full-display path
        // and avoiding stale/colliding cached popup data.
        if (IsCompactAnalyzerModeActive())
            return cached.CompactRows;

        return BuildFullInfoRowsUncached(item, mods, cached.Rating);
    }

    private int GetCachedRating(Entity item)
    {
        if (item == null)
            return 0;

        var key = unchecked((long)item.Address);

        if (_analysisCache.TryGetValue(key, out var cached))
            return cached.Rating;

        var rating = GetIFLQuality(item);
        _analysisCache[key] = new CachedItemAnalysis
        {
            Rating = rating
        };

        if (_analysisCache.Count > 512)
            _analysisCache.Clear();

        return rating;
    }

    private CachedItemAnalysis GetCachedAnalysis(Entity item, Mods mods = null)
    {
        if (item == null)
            return new CachedItemAnalysis
            {
                Rating = 0,
                CompactRows = new List<string>(),
                FullRows = new List<string>()
            };

        var key = unchecked((long)item.Address);

        _analysisCache.TryGetValue(key, out var cached);

        mods ??= item.GetComponent<Mods>();
        if (mods == null)
        {
            cached ??= new CachedItemAnalysis { Rating = 0 };
            _analysisCache[key] = cached;
            return cached;
        }

        // A rating-only cache entry is enough for inventory star drawing.
        // Only build the expensive popup rows when the popup is actually
        // requested for this item.
        if (cached != null && cached.CompactRows != null)
            return cached;

        var rating = cached?.Rating ?? GetIFLQuality(item);

        cached = new CachedItemAnalysis
        {
            Rating = rating,
            CompactRows = BuildCompactInfoRowsUncached(item, mods, rating),
            FullRows = null
        };

        _analysisCache[key] = cached;

        if (_analysisCache.Count > 512)
            _analysisCache.Clear();

        return cached;
    }

    private List<string> BuildCompactInfoRowsUncached(Entity item, Mods mods, int rating)
    {
        var rows = new List<string>();

        if (rating <= 0)
            return rows;

        var details = new List<string>();
        foreach (var detail in BuildUserDefinedRatingDetails(item, rating))
        {
            if (!string.IsNullOrWhiteSpace(detail))
                details.Add(detail);
        }

        if (details.Count == 0)
            return rows;

        rows.Add($"LOOT RATING|{rating}");
        rows.Add("DETAIL|QUALIFYING STATS");

        foreach (var detail in details)
            rows.Add($"DETAIL|{detail}");

        return rows;
    }

    private List<string> BuildFullInfoRowsUncached(Entity item, Mods mods, int cachedRating)
    {
        if (IsCompactAnalyzerModeActive())
            return BuildCompactInfoRows(item, mods);

        var rows = new List<string>();

        // Compact top section: put the item identity, rarity, affix counts,
        // item level, and defense into a small number of high-information rows.
        var displayName = GetItemDisplayName(item);

        // Some runtime name sources can already contain our UI row protocol.
        // Never let HEADER| or its trailing metadata leak into the visible name.
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var headerIndex = displayName.IndexOf("HEADER|", StringComparison.OrdinalIgnoreCase);
            if (headerIndex >= 0)
                displayName = displayName.Substring(headerIndex + "HEADER|".Length);

            var pipeIndex = displayName.IndexOf('|');
            if (pipeIndex >= 0)
                displayName = displayName.Substring(0, pipeIndex);

            displayName = displayName.Trim();
        }

        var rarity = mods.ItemRarity.ToString();
        var affixCounts = GetAffixCounts(mods);
        var rating = cachedRating;

        if (!string.IsNullOrWhiteSpace(displayName))
            rows.Add($"HEADER|{displayName}|{rarity}|{Math.Max(0, Math.Min(3, rating))}");

        var metaParts = new List<string>();

        if (true)
        {
            var ilvl = GetMemberString(item, "ItemLevel", "ItemLvl", "Level");
            if (!string.IsNullOrWhiteSpace(ilvl))
                metaParts.Add($"ilvl {ilvl}");
        }

        metaParts.Add($"{affixCounts.Prefixes} Prefixes");
        metaParts.Add($"{affixCounts.Suffixes} Suffixes");

        if (affixCounts.Unknown > 0)
            metaParts.Add($"{affixCounts.Unknown} Unknown");

        if (metaParts.Count > 0)
            rows.Add("META|" + string.Join("  |  ", metaParts));

        var itemSummary = BuildItemSummary(item, mods);
        foreach (var summaryRow in itemSummary)
            rows.Add(summaryRow);

        

        rows.Add(string.Empty);
        rows.Add("MODIFIERS");

        foreach (var row in BuildModRows(item, mods))
            rows.Add(row);

        if (mods.ItemRarity == ItemRarity.Rare)
        {
            if (rating > 0)
            {
                rows.Add(string.Empty);
                rows.Add($"LOOT RATING|{rating}");
                rows.Add("DETAIL|QUALIFYING STATS");

                if (true)
                {
                    foreach (var detail in BuildUserDefinedRatingDetails(item, rating))
                        rows.Add($"DETAIL|{detail}");
                }
            }
        }

        return rows;
    }


    private void RefreshAnalysisCacheIfSettingsChanged()
    {
        var signature = ComputeAnalysisSettingsSignature();

        if (signature == _analysisSettingsSignature)
            return;

        _analysisSettingsSignature = signature;
        _analysisCache.Clear();
    }

    private long ComputeAnalysisSettingsSignature()
    {
        unchecked
        {
            long hash = 1469598103934665603L;

            void Add(int value)
            {
                hash ^= value;
                hash *= 1099511628211L;
            }

            Add(Settings.Enable.Value ? 1 : 0);
            Add(Settings.ShowItemInfo.Value ? 1 : 0);
            Add(Settings.AlwaysShowItemInfo.Value ? 1 : 0);
            Add(Settings.ItemInfoCompactMode.Value ? 1 : 0);
            Add(Settings.ItemInfoDebugMode.Value ? 1 : 0);
            Add(Settings.ItemInfoWidth.Value);

            Add(Settings.Star_Weapon_DpsMode.Value);

            // Include all rating threshold nodes without reflection.
            Add(Settings.Star_Helmet_GoodRequired.Value);
            Add(Settings.Star_Helmet_GreatRequired.Value);
            Add(Settings.Star_Helmet_ExcellentRequired.Value);
            Add(Settings.Star_Helmet_Life.Value);
            Add(Settings.Star_Helmet_ColdResistance.Value);
            Add(Settings.Star_Helmet_FireResistance.Value);
            Add(Settings.Star_Helmet_LightningResistance.Value);
            Add(Settings.Star_Helmet_ChaosResistance.Value);
            Add(Settings.Star_Helmet_EnergyShield.Value);
            Add(Settings.Star_Helmet_SpellSuppression.Value);
            Add(Settings.Star_Helmet_Attributes.Value);

            Add(Settings.Star_BodyArmour_GoodRequired.Value);
            Add(Settings.Star_BodyArmour_GreatRequired.Value);
            Add(Settings.Star_BodyArmour_ExcellentRequired.Value);
            Add(Settings.Star_BodyArmour_Life.Value);
            Add(Settings.Star_BodyArmour_ColdResistance.Value);
            Add(Settings.Star_BodyArmour_FireResistance.Value);
            Add(Settings.Star_BodyArmour_LightningResistance.Value);
            Add(Settings.Star_BodyArmour_ChaosResistance.Value);
            Add(Settings.Star_BodyArmour_EnergyShield.Value);
            Add(Settings.Star_BodyArmour_SpellSuppression.Value);
            Add(Settings.Star_BodyArmour_Attributes.Value);

            Add(Settings.Star_Gloves_GoodRequired.Value);
            Add(Settings.Star_Gloves_GreatRequired.Value);
            Add(Settings.Star_Gloves_ExcellentRequired.Value);
            Add(Settings.Star_Gloves_Life.Value);
            Add(Settings.Star_Gloves_ColdResistance.Value);
            Add(Settings.Star_Gloves_FireResistance.Value);
            Add(Settings.Star_Gloves_LightningResistance.Value);
            Add(Settings.Star_Gloves_ChaosResistance.Value);
            Add(Settings.Star_Gloves_SpellSuppression.Value);
            Add(Settings.Star_Gloves_AttackSpeed.Value);
            Add(Settings.Star_Gloves_CastSpeed.Value);
            Add(Settings.Star_Gloves_CritMultiplier.Value);
            Add(Settings.Star_Gloves_Attributes.Value);

            Add(Settings.Star_Boots_GoodRequired.Value);
            Add(Settings.Star_Boots_GreatRequired.Value);
            Add(Settings.Star_Boots_ExcellentRequired.Value);
            Add(Settings.Star_Boots_Life.Value);
            Add(Settings.Star_Boots_ColdResistance.Value);
            Add(Settings.Star_Boots_FireResistance.Value);
            Add(Settings.Star_Boots_LightningResistance.Value);
            Add(Settings.Star_Boots_ChaosResistance.Value);
            Add(Settings.Star_Boots_SpellSuppression.Value);
            Add(Settings.Star_Boots_Attributes.Value);
            Add(Settings.Star_Boots_MovementSpeed.Value);

            Add(Settings.Star_Belt_GoodRequired.Value);
            Add(Settings.Star_Belt_GreatRequired.Value);
            Add(Settings.Star_Belt_ExcellentRequired.Value);
            Add(Settings.Star_Belt_Life.Value);
            Add(Settings.Star_Belt_ColdResistance.Value);
            Add(Settings.Star_Belt_FireResistance.Value);
            Add(Settings.Star_Belt_LightningResistance.Value);
            Add(Settings.Star_Belt_ChaosResistance.Value);
            Add(Settings.Star_Belt_Attributes.Value);
            Add(Settings.Star_Belt_Mana.Value);

            Add(Settings.Star_Ring_GoodRequired.Value);
            Add(Settings.Star_Ring_GreatRequired.Value);
            Add(Settings.Star_Ring_ExcellentRequired.Value);
            Add(Settings.Star_Ring_Life.Value);
            Add(Settings.Star_Ring_ColdResistance.Value);
            Add(Settings.Star_Ring_FireResistance.Value);
            Add(Settings.Star_Ring_LightningResistance.Value);
            Add(Settings.Star_Ring_ChaosResistance.Value);
            Add(Settings.Star_Ring_Attributes.Value);
            Add(Settings.Star_Ring_Mana.Value);
            Add(Settings.Star_Ring_AttackSpeed.Value);
            Add(Settings.Star_Ring_CastSpeed.Value);
            Add(Settings.Star_Ring_CritMultiplier.Value);
            Add(Settings.Star_Ring_Accuracy.Value);

            Add(Settings.Star_Amulet_GoodRequired.Value);
            Add(Settings.Star_Amulet_GreatRequired.Value);
            Add(Settings.Star_Amulet_ExcellentRequired.Value);
            Add(Settings.Star_Amulet_Life.Value);
            Add(Settings.Star_Amulet_ColdResistance.Value);
            Add(Settings.Star_Amulet_FireResistance.Value);
            Add(Settings.Star_Amulet_LightningResistance.Value);
            Add(Settings.Star_Amulet_ChaosResistance.Value);
            Add(Settings.Star_Amulet_Attributes.Value);
            Add(Settings.Star_Amulet_EnergyShield.Value);
            Add(Settings.Star_Amulet_GemLevels.Value);
            Add(Settings.Star_Amulet_CritMultiplier.Value);

            Add(Settings.Star_Shield_GoodRequired.Value);
            Add(Settings.Star_Shield_GreatRequired.Value);
            Add(Settings.Star_Shield_ExcellentRequired.Value);
            Add(Settings.Star_Shield_Life.Value);
            Add(Settings.Star_Shield_ColdResistance.Value);
            Add(Settings.Star_Shield_FireResistance.Value);
            Add(Settings.Star_Shield_LightningResistance.Value);
            Add(Settings.Star_Shield_ChaosResistance.Value);
            Add(Settings.Star_Shield_EnergyShield.Value);
            Add(Settings.Star_Shield_SpellSuppression.Value);
            Add(Settings.Star_Shield_Attributes.Value);

            Add(Settings.Star_Quiver_GoodRequired.Value);
            Add(Settings.Star_Quiver_GreatRequired.Value);
            Add(Settings.Star_Quiver_ExcellentRequired.Value);
            Add(Settings.Star_Quiver_Life.Value);
            Add(Settings.Star_Quiver_ColdResistance.Value);
            Add(Settings.Star_Quiver_FireResistance.Value);
            Add(Settings.Star_Quiver_LightningResistance.Value);
            Add(Settings.Star_Quiver_ChaosResistance.Value);
            Add(Settings.Star_Quiver_AttackSpeed.Value);
            Add(Settings.Star_Quiver_CritMultiplier.Value);
            Add(Settings.Star_Quiver_Attributes.Value);

            Add(Settings.Star_Weapon_GoodRequired.Value);
            Add(Settings.Star_Weapon_GreatRequired.Value);
            Add(Settings.Star_Weapon_ExcellentRequired.Value);
            Add(Settings.Star_Weapon_WeaponDps.Value);
            Add(Settings.Star_Weapon_AttackSpeed.Value);
            Add(Settings.Star_Weapon_CritMultiplier.Value);
            Add(Settings.Star_Weapon_ColdResistance.Value);
            Add(Settings.Star_Weapon_FireResistance.Value);
            Add(Settings.Star_Weapon_LightningResistance.Value);
            Add(Settings.Star_Weapon_Attributes.Value);
            Add(Settings.Star_Weapon_GemLevels.Value);

            return hash;
        }
    }

    private List<string> BuildItemSummary(Entity item, Mods mods)
    {
        var rows = new List<string>();

        // Read the authoritative item components instead of scraping the
        // rendered tooltip. This is the same data path used by the original
        // AdvancedTooltip weapon-DPS implementation.
        var quality = item?.GetComponent<Quality>();
        var armour = item?.GetComponent<Armour>();

        var itemLevel = mods?.ItemLevel ?? 0;
        var qualityValue = quality?.ItemQuality ?? -1;

        if (itemLevel > 0 || qualityValue >= 0)
        {
            var parts = new List<string>();
            if (itemLevel > 0)
                parts.Add($"iLvl {itemLevel}");
            if (qualityValue > 0)
                parts.Add($"Quality {qualityValue}%");
            rows.Add(string.Join("  |  ", parts));
        }

        if (armour != null)
        {
            var defenses = CalculateFinalDefenses(item, mods, armour);
            var parts = new List<string>();

            if (defenses.Armour > 0)
                parts.Add($"ARM {defenses.Armour}");
            if (defenses.Evasion > 0)
                parts.Add($"EVA {defenses.Evasion}");
            if (defenses.EnergyShield > 0)
                parts.Add($"ES {defenses.EnergyShield}");

            if (parts.Count > 0)
                rows.Add("DEFENSES|" + string.Join("  •  ", parts));
        }

        if (IsWeaponPath(item?.Path))
        {
            var dps = CalculateWeaponDps(item, mods);
            if (dps.Total > 0)
            {
                rows.Add($"DPSROW|PDPS {dps.Physical:0.0}|EDPS {dps.Elemental:0.0}|DPS {dps.Total:0.0}");
            }
        }

        return rows;
    }

    private (int Armour, int Evasion, int EnergyShield) CalculateFinalDefenses(
        Entity item,
        Mods mods,
        Armour armour)
    {
        try
        {
            // ArmourScore/EvasionScore/ESScore are the base item values exposed
            // by the Armour component. Rebuild the item's LOCAL defenses using
            // PoE's local-defense formula:
            // (base + local flat) * (1 + quality + local increased).
            //
            // Quality is local to the item and applies to its base defenses.
            double ar = armour.ArmourScore;
            double ev = armour.EvasionScore;
            double es = armour.EnergyShieldScore;

            var quality = item?.GetComponent<Quality>();
            var qualityPct = quality?.ItemQuality ?? 0;

            double arInc = qualityPct;
            double evInc = qualityPct;
            double esInc = qualityPct;

            double arFlat = 0;
            double evFlat = 0;
            double esFlat = 0;

            foreach (var mod in mods?.ItemMods ?? Enumerable.Empty<ItemMod>())
            {
                if (mod == null || string.IsNullOrEmpty(mod.RawName))
                    continue;

                ModsDat.ModRecord record;
                try
                {
                    record = GameController.Files.Mods.records[mod.RawName];
                }
                catch
                {
                    continue;
                }

                if (record == null)
                    continue;

                var values = GetMemberValues(mod)
                    .Select(ParseDouble)
                    .ToList();

                var pairs = record.StatNames
                    .Zip(values, (stat, value) => new { stat, value });

                foreach (var pair in pairs)
                {
                    if (pair.stat == null)
                        continue;

                    var stat = pair.stat.Key ?? string.Empty;
                    var value = pair.value;

                    if (value == 0)
                        continue;

                    // Flat local defense additions.
                    if (stat == "local_base_physical_damage_reduction_rating")
                    {
                        arFlat += value;
                        continue;
                    }

                    if (stat == "local_base_evasion_rating")
                    {
                        evFlat += value;
                        continue;
                    }

                    if (stat == "local_energy_shield")
                    {
                        esFlat += value;
                        continue;
                    }

                    // Hybrid flat Evasion + ES.
                    if (stat == "local_evasion_rating_and_energy_shield")
                    {
                        evFlat += value;
                        esFlat += value;
                        continue;
                    }

                    // Local increased defense percentages.
                    switch (stat)
                    {
                        case "local_physical_damage_reduction_rating_+%":
                            arInc += value;
                            break;

                        case "local_evasion_rating_+%":
                            evInc += value;
                            break;

                        case "local_energy_shield_+%":
                            esInc += value;
                            break;

                        case "local_armour_and_evasion_+%":
                            arInc += value;
                            evInc += value;
                            break;

                        case "local_armour_and_energy_shield_+%":
                            arInc += value;
                            esInc += value;
                            break;

                        case "local_evasion_and_energy_shield_+%":
                            evInc += value;
                            esInc += value;
                            break;

                        case "local_armour_and_evasion_and_energy_shield_+%":
                            arInc += value;
                            evInc += value;
                            esInc += value;
                            break;
                    }
                }
            }

            var finalAr = Math.Round((ar + arFlat) * (1.0 + arInc / 100.0));
            var finalEv = Math.Round((ev + evFlat) * (1.0 + evInc / 100.0));
            var finalEs = Math.Round((es + esFlat) * (1.0 + esInc / 100.0));

            return ((int)finalAr, (int)finalEv, (int)finalEs);
        }
        catch
        {
            // Never let the display-only calculation break the item tooltip.
            return (
                armour?.ArmourScore ?? 0,
                armour?.EvasionScore ?? 0,
                armour?.EnergyShieldScore ?? 0);
        }
    }

    private (double Physical, double Elemental, double Total) CalculateWeaponDps(Entity item, Mods mods)
    {
        try
        {
            var weapon = item?.GetComponent<Weapon>();
            if (weapon == null || weapon.AttackTime <= 0)
                return (0, 0, 0);

            // This follows the actual AdvancedTooltip implementation:
            // Weapon.DamageMin/Max + AttackTime, then local weapon mods.
            var attacksPerSecond = 1000.0 / weapon.AttackTime;
            double physicalLow = weapon.DamageMin;
            double physicalHigh = weapon.DamageMax;

            var elemental = new double[(int)DamageType.Chaos + 1];
            var physicalMultiplier = 1.0;

            foreach (var mod in mods?.ItemMods ?? Enumerable.Empty<ItemMod>())
            {
                if (mod == null || string.IsNullOrEmpty(mod.RawName))
                    continue;

                ModsDat.ModRecord record;
                try
                {
                    record = GameController.Files.Mods.records[mod.RawName];
                }
                catch
                {
                    continue;
                }

                if (record == null)
                    continue;

                // StatValues are exposed as strings in this ExileCore build.
                // AdvancedTooltip gets its numeric values from ModValue.StatValue;
                // we reproduce that conversion here.
                var rawValues = GetMemberValues(mod);
                var numericValues = rawValues
                    .Select(ParseDouble)
                    .ToList();

                foreach (var pair in record.StatNames
                    .Zip(record.StatRange, (stat, range) => new { stat, range })
                    .Zip(numericValues, (pair, value) => new { pair.stat, pair.range, value }))
                {
                    var stat = pair.stat;
                    var range = pair.range;
                    var value = pair.value;

                    if (stat == null)
                        continue;

                    if (range.Min == 0 && range.Max == 0)
                        continue;

                    if (value <= -1000)
                        continue;

                    switch (stat.Key)
                    {
                        case "physical_damage_+%":
                        case "local_physical_damage_+%":
                            physicalMultiplier += value / 100.0;
                            break;

                        case "local_attack_speed_+%":
                            attacksPerSecond *= (100.0 + value) / 100.0;
                            break;

                        case "local_minimum_added_physical_damage":
                            physicalLow += value;
                            break;

                        case "local_maximum_added_physical_damage":
                            physicalHigh += value;
                            break;

                        case "local_minimum_added_fire_damage":
                        case "local_maximum_added_fire_damage":
                        case "unique_local_minimum_added_fire_damage_when_in_main_hand":
                        case "unique_local_maximum_added_fire_damage_when_in_main_hand":
                            elemental[(int)DamageType.Fire] += value;
                            break;

                        case "local_minimum_added_cold_damage":
                        case "local_maximum_added_cold_damage":
                        case "unique_local_minimum_added_cold_damage_when_in_off_hand":
                        case "unique_local_maximum_added_cold_damage_when_in_off_hand":
                            elemental[(int)DamageType.Cold] += value;
                            break;

                        case "local_minimum_added_lightning_damage":
                        case "local_maximum_added_lightning_damage":
                            elemental[(int)DamageType.Lightning] += value;
                            break;

                        case "unique_local_minimum_added_chaos_damage_when_in_off_hand":
                        case "unique_local_maximum_added_chaos_damage_when_in_off_hand":
                        case "local_minimum_added_chaos_damage":
                        case "local_maximum_added_chaos_damage":
                            elemental[(int)DamageType.Chaos] += value;
                            break;
                    }
                }
            }

            var quality = item.GetComponent<Quality>();
            if (quality == null)
                return (0, 0, 0);

            physicalMultiplier += quality.ItemQuality / 100.0;

            physicalLow = Math.Round(physicalLow * physicalMultiplier);
            physicalHigh = Math.Round(physicalHigh * physicalMultiplier);

            var physicalDps = ((physicalLow + physicalHigh) / 2.0) * attacksPerSecond;

            double elementalDps = 0;
            for (var i = (int)DamageType.Fire; i <= (int)DamageType.Chaos && i < elemental.Length; i++)
                elementalDps += (elemental[i] / 2.0) * attacksPerSecond;

            return (physicalDps, elementalDps, physicalDps + elementalDps);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static double ParseDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        value = value.Trim().Replace(",", "");

        return double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;
    }


    private List<string> GetCurrentTooltipLines(Entity item)
    {
        var result = new List<string>();

        try
        {
            var icon = _hoverItemIcon;
            var tooltip = icon?.ItemFrame;

            if (tooltip == null || !tooltip.IsValid)
                return result;

            if (icon.Item == null || item == null || icon.Item.Address != item.Address)
                return result;

            // Avoid version-specific ExileCore helper methods here. Walk the
            // tooltip's Children collection through the same reflection helpers
            // already used elsewhere in this plugin.
            CollectTooltipText(tooltip, result, 0, new HashSet<object>());
        }
        catch
        {
        }

        return result;
    }

    private void CollectTooltipText(object obj, List<string> result, int depth, HashSet<object> visited)
    {
        if (obj == null || depth > 8 || result == null)
            return;

        if (!visited.Add(obj))
            return;

        var text = GetMemberString(obj, "Text");
        if (!string.IsNullOrWhiteSpace(text) && !result.Contains(text.Trim()))
            result.Add(text.Trim());

        var children = GetMemberObject(obj, "Children");
        if (children is System.Collections.IEnumerable enumerable && !(children is string))
        {
            foreach (var child in enumerable)
                CollectTooltipText(child, result, depth + 1, visited);
        }
    }


    private static int FindFirstNumber(IEnumerable<string> lines, params string[] patterns)
    {
        if (lines == null)
            return -1;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    line,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!match.Success)
                    continue;

                var raw = match.Groups[1].Value.Replace(",", "");
                if (int.TryParse(raw, out var value))
                    return value;
            }
        }

        return -1;
    }

    private static bool TryGetDamageRange(string line, out double min, out double max)
    {
        min = 0;
        max = 0;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        var match = System.Text.RegularExpressions.Regex.Match(
            line,
            @"(?:Damage|damage)\s*:\s*([\d,]+)\s*-\s*([\d,]+)");

        if (!match.Success)
            return false;

        if (!double.TryParse(match.Groups[1].Value.Replace(",", ""),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out min))
            return false;

        if (!double.TryParse(match.Groups[2].Value.Replace(",", ""),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out max))
            return false;

        return true;
    }

    private static double GetWeaponDps(IEnumerable<string> lines)
    {
        if (lines == null)
            return 0;

        double totalAverageHit = 0;
        double attacksPerSecond = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var aps = System.Text.RegularExpressions.Regex.Match(
                line,
                @"Attacks?\s+per\s+Second:\s*([0-9]+(?:\.[0-9]+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (aps.Success)
            {
                double.TryParse(
                    aps.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out attacksPerSecond);
                continue;
            }

            if (TryGetDamageRange(line, out var min, out var max))
                totalAverageHit += (min + max) / 2.0;
        }

        if (attacksPerSecond <= 0 || totalAverageHit <= 0)
            return 0;

        return totalAverageHit * attacksPerSecond;
    }

    private static bool IsWeaponPath(string path)
    {
        return !string.IsNullOrEmpty(path) &&
               path.IndexOf("/Weapons/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private List<string> BuildDebugRows(Entity item, Mods mods)
    {
        var rows = new List<string>
        {
            "ITEM INFO DEBUG",
            $"Item type: {item.GetType().FullName}",
            $"Mods type: {mods.GetType().FullName}",
            $"Path: {item.Path ?? "<null>"}",
            $"Rarity: {mods.ItemRarity}",
            $"ItemMods count: {(mods.ItemMods == null ? 0 : mods.ItemMods.Count)}",
            string.Empty,
            "ITEM SELECTED MEMBERS"
        };

        AddSelectedMembers(rows, item, new[]
        {
            "RenderName", "Name", "DisplayName", "Text", "ItemLevel", "ItemLvl", "Level",
            "Path", "Metadata", "Address", "EntityId", "Id", "Type", "ClientRect"
        });

        rows.Add($"Item available members: {GetMemberNames(item, 500)}");
        rows.Add(string.Empty);
        rows.Add("MOD COLLECTION SELECTED MEMBERS");
        AddSelectedMembers(rows, mods, new[]
        {
            "ItemRarity", "ItemMods", "Prefixes", "Suffixes", "ImplicitMods", "ExplicitMods"
        });
        rows.Add($"Mods available members: {GetMemberNames(mods, 500)}");

        if (mods.ItemMods != null)
        {
            var index = 0;
            foreach (var mod in mods.ItemMods)
            {
                if (mod == null)
                    continue;

                rows.Add(string.Empty);
                rows.Add($"MOD {index}: {mod.GetType().FullName}");
                AddSelectedMembers(rows, mod, new[]
                {
                    "RawName", "Name", "DisplayName", "Text", "Tier", "TierName", "TierText",
                    "Values", "Value", "Range", "Stat", "Stats", "Mod", "Id", "Key",
                    "Domain", "Group", "GenerationType", "AffixName"
                });
                rows.Add($"Mod available members: {GetMemberNames(mod, 900)}");
                index++;

                // Four mods is enough to identify the runtime's mod object without
                // making the debug panel impossibly tall.
                if (index >= 4)
                {
                    if (mods.ItemMods.Count > 4)
                        rows.Add($"... {mods.ItemMods.Count - 4} more mods omitted ...");
                    break;
                }
            }
        }

        rows.Add(string.Empty);
        rows.Add("TIP: send this screenshot; DEBUG can be turned off afterward");
        return rows;
    }

    private static string GetMemberNames(object obj, int maxLength)
    {
        if (obj == null)
            return "<null>";

        try
        {
            var type = obj.GetType();
            var names = new List<string>();

            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                names.Add("P:" + prop.Name);

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                names.Add("F:" + field.Name);

            names = names.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return TrimDebug(string.Join(", ", names), maxLength);
        }
        catch (Exception ex)
        {
            return $"<error {ex.GetType().Name}>";
        }
    }

    private static void AddSelectedMembers(List<string> rows, object obj, IEnumerable<string> names)
    {
        if (obj == null)
            return;

        foreach (var name in names)
        {
            var found = false;
            try
            {
                var type = obj.GetType();
                var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    var value = prop.GetValue(obj);
                    rows.Add($"{name}: {FormatDebugValue(value)}");
                    found = true;
                }

                if (!found)
                {
                    var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        var value = field.GetValue(obj);
                        rows.Add($"{name}: {FormatDebugValue(value)}");
                        found = true;
                    }
                }
            }
            catch (Exception ex)
            {
                rows.Add($"{name}: <error {ex.GetType().Name}>");
            }
        }
    }

    private static string FormatDebugValue(object value)
    {
        if (value == null)
            return "<null>";

        try
        {
            if (value is string text)
                return TrimDebug(text, 180);

            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                var parts = new List<string>();
                var count = 0;
                foreach (var entry in enumerable)
                {
                    parts.Add(entry?.ToString() ?? "<null>");
                    count++;
                    if (count >= 8)
                    {
                        parts.Add("...");
                        break;
                    }
                }
                return TrimDebug("[" + string.Join(", ", parts) + "]", 180);
            }

            return TrimDebug(value.ToString() ?? "<null>", 180);
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static string TrimDebug(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;
        return text.Substring(0, max - 3) + "...";
    }

    private void DrawItemInfoOverlay(Entity item, SharpDX.RectangleF itemRect)
    {
        var mods = item.GetComponent<Mods>();
        if (mods == null)
            return;

        var draw = ImGuiNET.ImGui.GetForegroundDrawList();
        var rows = Settings.ItemInfoDebugMode.Value
            ? BuildDebugRows(item, mods)
            : BuildNormalInfoRows(item, mods);

        // Compact mode can legitimately return no rows when the item has a
        // rating but no qualifying-stat details to display. Do not create an
        // empty background panel in that case.
        if (IsCompactAnalyzerModeActive() && rows.Count == 0)
            return;

        var font = ImGuiNET.ImGui.GetFont();
        var fontSize = ImGuiNET.ImGui.GetFontSize();
        var glyphHeight = Math.Max(1f, ImGuiNET.ImGui.CalcTextSize("Ag").Y);

        const float rowGap = 9f;
        const float blankGap = 10f;
        var rowStep = glyphHeight + rowGap;
        var padding = IsCompactAnalyzerModeActive() ? 9f : 10f;

        // Sanitize every displayed row to one physical line.
        for (var i = 0; i < rows.Count; i++)
        {
            if (!string.IsNullOrEmpty(rows[i]))
                rows[i] = rows[i].Replace("\r", " ").Replace("\n", " ");
        }

        // Width is based on the longest actual row, with a sensible cap.
        var maxWidth = 0f;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row))
                continue;

            var measure = row;
            if (measure.StartsWith("DETAIL|", StringComparison.Ordinal))
                measure = measure.Substring("DETAIL|".Length);
            else if (measure.StartsWith("SUMMARY|", StringComparison.Ordinal))
                measure = measure.Substring("SUMMARY|".Length).Replace("|", "  |  ");
            else if (measure.StartsWith("DPSROW|", StringComparison.Ordinal))
                measure = measure.Substring("DPSROW|".Length).Replace("|", "  |  ");

            maxWidth = Math.Max(maxWidth, ImGuiNET.ImGui.CalcTextSize(measure).X);
        }

        var width = IsCompactAnalyzerModeActive()
            ? Math.Max(235f, (int)Math.Ceiling(maxWidth + padding * 2f + 12f))
            : Math.Max(Settings.ItemInfoWidth.Value,
                (int)Math.Ceiling(maxWidth + padding * 2f + 12f));
        width = Math.Min(width, 760);

        var height = padding * 2f;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row))
                height += blankGap;
            else
                height += rowStep;
        }

        var screen = ImGuiNET.ImGui.GetIO().DisplaySize;
        var x = itemRect.Right + 12f;
        var y = itemRect.Top;

        if (x + width > screen.X)
            x = itemRect.Left - width - 12f;
        x = Math.Max(4f, x);

        if (y + height > screen.Y)
            y = Math.Max(4f, screen.Y - height - 4f);

        var bg = ToImGuiColor(GetColor(Settings.ItemInfoBackground, new Color(15,15,20,255)));
        var border = ToImGuiColor(GetColor(Settings.ItemInfoBorder, new Color(110,110,125,255)));
        var tier = ToImGuiColor(GetColor(Settings.ItemInfoTierColor, Color.Gold));

        draw.AddRectFilled(new Vector2(x, y), new Vector2(x + width, y + height), bg, 5f);
        draw.AddRect(new Vector2(x, y), new Vector2(x + width, y + height),
            border, 5f, ImGuiNET.ImDrawFlags.None, 1.5f);

        // Hard clip to the panel. A long modifier can never draw over the next
        // row or outside the overlay.
        draw.PushClipRect(new Vector2(x + 1f, y + 1f),
            new Vector2(x + width - 1f, y + height - 1f), true);

        var cy = y + padding;

        var isFirstRow = true;
        var currentRating = GetCachedAnalysis(item, mods).Rating;

        foreach (var raw in rows)
        {
            if (string.IsNullOrEmpty(raw))
            {
                cy += blankGap;
                isFirstRow = false;
                continue;
            }

            var text = raw;

            if (text.StartsWith("S ", StringComparison.Ordinal) && text.Contains(" | P "))
            {
                var parts = text.Split(new[] { " | " }, StringSplitOptions.None);
                var sText = parts[0];
                var pText = parts.Length > 1 ? parts[1] : "";

                var sColor = ToImGuiColor(new Color(190,75,230,255));
                var pColor = ToImGuiColor(new Color(70,150,255,255));
                var gray = ToImGuiColor(new Color(150,150,160,255));

                draw.AddText(font, fontSize, new Vector2(x + padding, cy), sColor, sText);
                var sx = x + padding + ImGuiNET.ImGui.CalcTextSize(sText).X + 8f;
                draw.AddText(font, fontSize, new Vector2(sx, cy), gray, "|");
                sx += ImGuiNET.ImGui.CalcTextSize("|").X + 8f;
                draw.AddText(font, fontSize, new Vector2(sx, cy), pColor, pText);

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("HEADER|", StringComparison.Ordinal))
            {
                var parts = text.Substring("HEADER|".Length).Split('|');
                var name = parts.Length > 0 ? parts[0] : "Item";
                var rarity = parts.Length > 1 ? parts[1] : "";
                var rating = 0;
                if (parts.Length > 2)
                    int.TryParse(parts[2], out rating);

                var titleColor = ToImGuiColor(new Color(225, 225, 230, 255));
                var rarityColor = ToImGuiColor(new Color(205, 205, 212, 255));

                var sx = x + padding;

                draw.AddText(font, fontSize * 1.02f,
                    new Vector2(sx, cy), titleColor, name);
                sx += ImGuiNET.ImGui.CalcTextSize(name).X + 10f;

                draw.AddText(font, fontSize * 1.02f,
                    new Vector2(sx, cy), rarityColor, "|");
                sx += ImGuiNET.ImGui.CalcTextSize("|").X + 10f;

                draw.AddText(font, fontSize * 1.02f,
                    new Vector2(sx, cy), rarityColor, rarity);
                sx += ImGuiNET.ImGui.CalcTextSize(rarity).X + 14f;

                // Always show all three rating stars in the header. Filled
                // stars are gold; empty stars are subtle hollow outlines.
                for (var star = 0; star < 3; star++)
                {
                    var center = new Vector2(
                        sx + star * 16f + 5f,
                        cy + glyphHeight * .5f);

                    if (star < Math.Min(3, Math.Max(0, rating)))
                    {
                        DrawFivePointStar(draw, center, 4.8f,
                            ToImGuiColor(new Color(245, 195, 60, 255)));
                    }
                    else
                    {
                        DrawFivePointStarOutline(draw, center, 4.8f,
                            ToImGuiColor(new Color(90, 90, 100, 255)));
                    }
                }

                cy += rowStep * .95f;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("META|", StringComparison.Ordinal))
            {
                var meta = text.Substring("META|".Length);
                draw.AddText(font, fontSize * .78f,
                    new Vector2(x + padding, cy),
                    ToImGuiColor(new Color(145, 145, 155, 255)), meta);

                cy += rowStep * .88f;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("DEFENSES|", StringComparison.Ordinal))
            {
                var defense = text.Substring("DEFENSES|".Length);
                draw.AddText(font, fontSize * .84f,
                    new Vector2(x + padding, cy),
                    ToImGuiColor(new Color(195, 200, 210, 255)), defense);

                cy += rowStep * .9f;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("SUMMARY|", StringComparison.Ordinal))
            {
                var parts = text.Substring("SUMMARY|".Length).Split('|');
                var sx = x + padding;

                foreach (var part in parts)
                {
                    var c = ToImGuiColor(new Color(190,195,205,255));
                    if (part.StartsWith("Quality", StringComparison.Ordinal))
                        c = ToImGuiColor(new Color(220,205,120,255));
                    else if (part.StartsWith("ARM", StringComparison.Ordinal))
                        c = ToImGuiColor(new Color(180,195,205,255));
                    else if (part.StartsWith("EVA", StringComparison.Ordinal))
                        c = ToImGuiColor(new Color(70,210,105,255));
                    else if (part.StartsWith("ES", StringComparison.Ordinal))
                        c = ToImGuiColor(new Color(70,220,240,255));

                    draw.AddText(font, fontSize * .88f, new Vector2(sx, cy), c, part);
                    sx += ImGuiNET.ImGui.CalcTextSize(part).X + 18f;
                }

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("LOOT RATING|", StringComparison.Ordinal))
            {
                var ratingText = text.Substring("LOOT RATING|".Length);
                int.TryParse(ratingText, out var rating);

                var headingColor = ToImGuiColor(new Color(245, 205, 70, 255));
                draw.AddText(font, fontSize, new Vector2(x + padding, cy),
                    headingColor, "LOOT RATING");

                var headingWidth = ImGuiNET.ImGui.CalcTextSize("LOOT RATING").X;
                var sx = x + padding + headingWidth + 16f;
                var starColor = rating >= 3
                    ? ToImGuiColor(new Color(255, 215, 60, 255))
                    : rating == 2
                        ? ToImGuiColor(new Color(100, 215, 125, 255))
                        : ToImGuiColor(new Color(90, 170, 240, 255));

                for (var star = 0; star < 3; star++)
                {
                    var filled = star < Math.Min(3, Math.Max(0, rating));
                    var center = new Vector2(sx + star * 17f, cy + glyphHeight * .5f);
                    if (filled)
                        DrawFivePointStar(draw, center, 6.5f, starColor);
                    else
                        draw.AddCircle(center, 4.2f,
                            ToImGuiColor(new Color(75, 75, 88, 255)), 10, 1.2f);
                }

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("DEFENSES|", StringComparison.Ordinal))
            {
                var parts = text.Substring("DEFENSES|".Length).Split('|');
                var sx = x + padding;

                foreach (var part in parts)
                {
                    var isArm = part.StartsWith("ARM ", StringComparison.Ordinal);
                    var isEva = part.StartsWith("EVA ", StringComparison.Ordinal);
                    var isEs = part.StartsWith("ES ", StringComparison.Ordinal);

                    var valueText = isArm
                        ? part.Substring(4).Trim()
                        : isEva
                            ? part.Substring(4).Trim()
                            : part.Substring(3).Trim();

                    var c = isArm
                        ? ToImGuiColor(new Color(190, 200, 210, 255))
                        : isEva
                            ? ToImGuiColor(new Color(90, 205, 115, 255))
                            : ToImGuiColor(new Color(90, 215, 235, 255));

                    var center = new Vector2(sx + 7f, cy + glyphHeight * 0.5f);

                    if (isArm)
                    {
                        // Shield/armor crest.
                        var pts = new Vector2[]
                        {
                            new Vector2(center.X, center.Y - 7f),
                            new Vector2(center.X + 6f, center.Y - 4f),
                            new Vector2(center.X + 5f, center.Y + 3f),
                            new Vector2(center.X, center.Y + 7f),
                            new Vector2(center.X - 5f, center.Y + 3f),
                            new Vector2(center.X - 6f, center.Y - 4f)
                        };
                        draw.AddPolyline(ref pts[0], pts.Length, c, ImGuiNET.ImDrawFlags.Closed, 1.7f);
                    }
                    else if (isEva)
                    {
                        // Small green leaf/wing shape.
                        draw.AddLine(new Vector2(center.X - 6f, center.Y + 4f),
                                     new Vector2(center.X + 5f, center.Y - 5f), c, 1.8f);
                        draw.AddLine(new Vector2(center.X - 1f, center.Y + 4f),
                                     new Vector2(center.X + 5f, center.Y - 5f), c, 1.4f);
                    }
                    else if (isEs)
                    {
                        // Cyan energy diamond.
                        var pts = new Vector2[]
                        {
                            new Vector2(center.X, center.Y - 7f),
                            new Vector2(center.X + 6f, center.Y),
                            new Vector2(center.X, center.Y + 7f),
                            new Vector2(center.X - 6f, center.Y)
                        };
                        draw.AddPolyline(ref pts[0], pts.Length, c, ImGuiNET.ImDrawFlags.Closed, 1.7f);
                    }

                    draw.AddText(font, fontSize, new Vector2(sx + 17f, cy), c, valueText);
                    sx += ImGuiNET.ImGui.CalcTextSize(valueText).X + 44f;
                }

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("DPSROW|", StringComparison.Ordinal))
            {
                var parts = text.Substring("DPSROW|".Length).Split('|');
                var sx = x + padding;

                foreach (var part in parts)
                {
                    var isPdps = part.StartsWith("PDPS", StringComparison.Ordinal);
                    var isEdps = part.StartsWith("EDPS", StringComparison.Ordinal);
                    var numberText = isPdps
                        ? part.Substring(4).Trim()
                        : isEdps
                            ? part.Substring(4).Trim()
                            : part.Substring(3).Trim();

                    var c = ToImGuiColor(new Color(225, 225, 230, 255));
                    var center = new Vector2(sx + 7f, cy + glyphHeight * 0.5f);

                    if (isPdps)
                    {
                        // Crossed swords / physical damage.
                        draw.AddLine(new Vector2(center.X - 5f, center.Y + 5f),
                                     new Vector2(center.X + 5f, center.Y - 5f), c, 2.0f);
                        draw.AddLine(new Vector2(center.X - 5f, center.Y - 5f),
                                     new Vector2(center.X + 5f, center.Y + 5f), c, 1.4f);
                    }
                    else if (isEdps)
                    {
                        // Elemental spark.
                        draw.AddCircleFilled(center, 4.5f, c);
                        draw.AddLine(new Vector2(center.X, center.Y - 7f),
                                     new Vector2(center.X, center.Y + 7f), c, 1.4f);
                        draw.AddLine(new Vector2(center.X - 6f, center.Y),
                                     new Vector2(center.X + 6f, center.Y), c, 1.4f);
                    }
                    else
                    {
                        // Total DPS: compact target/burst.
                        draw.AddCircle(center, 5.5f, c, 10, 1.5f);
                        draw.AddCircleFilled(center, 2.2f, c);
                    }

                    draw.AddText(font, fontSize, new Vector2(sx + 17f, cy), c, numberText);
                    sx += ImGuiNET.ImGui.CalcTextSize(numberText).X + 44f;
                }

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("DETAIL|", StringComparison.Ordinal))
            {
                var detail = text.Substring("DETAIL|".Length).Trim();

                if (string.Equals(detail, "QUALIFYING STATS", StringComparison.Ordinal))
                {
                    var c = ToImGuiColor(new Color(185, 195, 205, 255));
                    draw.AddText(font, fontSize * .90f,
                        new Vector2(x + padding, cy), c, "QUALIFYING STATS");
                    cy += rowStep;
                    isFirstRow = false;
                    continue;
                }

                var cDetail = ToImGuiColor(new Color(205, 205, 212, 255));
                var iconCenter = new Vector2(
                    x + padding + 7f,
                    cy + glyphHeight * .5f);

                draw.AddCircleFilled(iconCenter, 3.2f,
                    ToImGuiColor(new Color(125, 125, 135, 255)));

                draw.AddText(font, fontSize * .94f,
                    new Vector2(x + padding + 18f, cy),
                    cDetail,
                    detail);

                cy += rowStep * 1.05f;
                isFirstRow = false;
                continue;
            }

            // Keep the existing modifier text, but pull [Tn] into a
            // right-aligned tag without changing the underlying data.
            if (text != "MODIFIERS" && text.Contains("[T") && text.EndsWith("]"))
            {
                var tagStart = text.LastIndexOf(" [T", StringComparison.Ordinal);
                if (tagStart > 0)
                {
                    var modText = text.Substring(0, tagStart);
                    var tag = text.Substring(tagStart + 1);

                    // T1 is special: highlight the entire modifier and its
                    // tier tag in gold. Every other tier remains neutral.
                    var isTierOne = string.Equals(tag, "[T1]", StringComparison.OrdinalIgnoreCase);
                    var modColor = isTierOne
                        ? ToImGuiColor(new Color(245, 205, 70, 255))
                        : ToImGuiColor(new Color(225, 225, 230, 255));

                    draw.AddText(font, fontSize, new Vector2(x + padding, cy), modColor, modText);

                    var tagColor = GetTierTagColor(tag);
                    var tagWidth = ImGuiNET.ImGui.CalcTextSize(tag).X;
                    draw.AddText(font, fontSize, new Vector2(x + width - padding - tagWidth, cy), tagColor, tag);

                    cy += rowStep;
                    isFirstRow = false;
                    continue;
                }
            }

            var summaryColor = ToImGuiColor(new Color(225, 225, 230, 255));

            var color = text == "MODIFIERS" || text.StartsWith("LOOT RATING:", StringComparison.Ordinal)
                ? tier
                : summaryColor;

            // Normal modifier lines are neutral; T1 modifiers are gold. Loot Rating
            // remains the primary star-based evaluation element.
            if (text != "MODIFIERS" &&
                !text.StartsWith("LOOT RATING:", StringComparison.Ordinal) &&
                !text.StartsWith("DEFENSES|", StringComparison.Ordinal) &&
                !text.StartsWith("WEAPON DPS:", StringComparison.Ordinal))
            {
                color = ToImGuiColor(new Color(225, 225, 230, 255));
            }

            draw.AddText(font, fontSize, new Vector2(x + padding, cy), color, text);
            cy += rowStep;
            isFirstRow = false;
        }
        draw.PopClipRect();
    }


    private static void DrawFivePointStar(ImGuiNET.ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        var points = new Vector2[10];
        const double start = -Math.PI / 2.0;

        for (var i = 0; i < 10; i++)
        {
            var r = (i % 2 == 0) ? radius : radius * 0.45f;
            var a = start + i * Math.PI / 5.0;
            points[i] = new Vector2(
                center.X + (float)Math.Cos(a) * r,
                center.Y + (float)Math.Sin(a) * r);
        }

        draw.AddConvexPolyFilled(ref points[0], points.Length, color);
    }

    private static void DrawFivePointStarOutline(
        ImGuiNET.ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        var points = new Vector2[10];
        const double start = -Math.PI / 2.0;

        for (var i = 0; i < 10; i++)
        {
            var r = (i % 2 == 0) ? radius : radius * 0.45f;
            var a = start + i * Math.PI / 5.0;
            points[i] = new Vector2(
                center.X + (float)Math.Cos(a) * r,
                center.Y + (float)Math.Sin(a) * r);
        }

        for (var i = 0; i < 10; i++)
        {
            var next = (i + 1) % 10;
            draw.AddLine(points[i], points[next], color, 1.1f);
        }
    }

    private uint GetTierTagColor(string tag)
    {
        if (string.Equals(tag, "[T1]", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(245, 205, 70, 255));

        return ToImGuiColor(new Color(175, 175, 185, 255));
    }

    private static bool IsRatingDetail(string text)
    {
        return Contains(text, "Life:") ||
               Contains(text, "Fire Resistance:") ||
               Contains(text, "Cold Resistance:") ||
               Contains(text, "Lightning Resistance:") ||
               Contains(text, "Chaos Resistance:") ||
               Contains(text, "All Resistances:");
    }

    private void DrawStatIcon(ImGuiNET.ImDrawListPtr draw, Vector2 center, string text)
    {
        var color = GetItemInfoTextColor(text, ToImGuiColor(SharpDX.Color.White));
        var radius = 5.5f;

        if (Contains(text, "Life:"))
        {
            // Simple heart-like diamond/chevron built from filled geometry.
            draw.AddCircleFilled(new Vector2(center.X - 3f, center.Y - 1f), radius * .65f, color);
            draw.AddCircleFilled(new Vector2(center.X + 3f, center.Y - 1f), radius * .65f, color);
            draw.AddTriangleFilled(
                new Vector2(center.X - 7f, center.Y),
                new Vector2(center.X + 7f, center.Y),
                new Vector2(center.X, center.Y + 8f), color);
            return;
        }

        if (Contains(text, "Cold Resistance:"))
        {
            for (var i = 0; i < 6; i++)
            {
                var a = i * (float)Math.PI / 3f;
                var dx = (float)Math.Cos(a) * 7f;
                var dy = (float)Math.Sin(a) * 7f;
                draw.AddLine(center, new Vector2(center.X + dx, center.Y + dy), color, 2f);
            }
            return;
        }

        if (Contains(text, "Lightning Resistance:"))
        {
            var p1 = new Vector2(center.X + 2f, center.Y - 8f);
            var p2 = new Vector2(center.X - 5f, center.Y + 1f);
            var p3 = new Vector2(center.X, center.Y + 1f);
            var p4 = new Vector2(center.X - 2f, center.Y + 8f);
            draw.AddLine(p1, p2, color, 2.5f);
            draw.AddLine(p2, p3, color, 2.5f);
            draw.AddLine(p3, p4, color, 2.5f);
            return;
        }

        if (Contains(text, "Fire Resistance:"))
        {
            draw.AddCircleFilled(center, 6f, color);
            draw.AddTriangleFilled(
                new Vector2(center.X - 5f, center.Y + 3f),
                new Vector2(center.X + 5f, center.Y + 3f),
                new Vector2(center.X, center.Y - 8f), color);
            return;
        }

        // Chaos / all-res fallback icon.
        draw.AddCircle(center, 6f, color, 12, 2f);
        draw.AddCircle(center, 2.5f, color, 8, 2f);
    }


    /// <summary>
    /// Color-codes the Life/Resistance stats in the Item Info overlay while
    /// leaving unrelated modifiers white. Life is intentionally red so it is
    /// visually distinct from Fire Resistance, which uses orange.
    ///
    /// Colors:
    ///   Life        = red
    ///   Fire        = orange
    ///   Cold        = blue
    ///   Lightning   = yellow
    ///   Chaos       = purple
    /// </summary>
    private uint GetItemInfoTextColor(string text, uint fallback)
    {
        return fallback;
    }


    private (int Prefixes, int Suffixes, int Unknown) GetAffixCounts(Mods mods)
    {
        if (mods?.ItemMods == null)
            return (0, 0, 0);

        var prefixes = 0;
        var suffixes = 0;
        var unknown = 0;

        foreach (var mod in mods.ItemMods)
        {
            if (mod == null || string.IsNullOrEmpty(mod.RawName))
                continue;

            try
            {
                // Authoritative ExileCore data path used by AdvancedTooltipPlus:
                // ItemMod.RawName -> GameController.Files.Mods.records[RawName]
                // -> ModsDat.ModRecord.AffixType.
                ModsDat.ModRecord record = GameController.Files.Mods.records[mod.RawName];

                if (record == null)
                {
                    unknown++;
                    continue;
                }

                if (record.AffixType == ModType.Prefix)
                    prefixes++;
                else if (record.AffixType == ModType.Suffix)
                    suffixes++;
            }
            catch
            {
                unknown++;
            }
        }

        return (prefixes, suffixes, unknown);
    }


    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
private bool IsItemInfoPopupVisible()
    {
        if (Settings.AlwaysShowItemInfo)
            return true;

        var key = (int)Settings.ItemInfoHotkey.Value;
        if (key == 0)
            return false;

        return (GetAsyncKeyState(key & 0xFF) & 0x8000) != 0;
    }

    private static bool IsCraftedMod(ItemMod mod)
    {
        if (mod == null)
            return false;

        try
        {
            // Different ExileCore versions expose crafted state differently,
            // so check the common boolean/property names without taking a
            // compile-time dependency on one particular version.
            foreach (var name in new[] { "IsCrafted", "Crafted", "IsCraftedMod" })
            {
                var value = GetMemberObject(mod, name);
                if (value is bool b && b)
                    return true;
            }

            // Some versions expose the source/type as a string or enum.
            foreach (var name in new[] { "Source", "ModSource", "Type", "ModType" })
            {
                var value = GetMemberObject(mod, name);
                if (value != null &&
                    value.ToString().IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            // Final data-driven fallback: crafted mod IDs in PoE's mod data
            // commonly carry the crafted marker in their raw key/name.
            var raw = mod.RawName ?? string.Empty;
            if (raw.IndexOf("crafted", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var record = GetMemberObject(mod, "ModRecord");
            var recordName = GetMemberString(record, "Key", "Name", "Id", "RawName");
            if (!string.IsNullOrEmpty(recordName) &&
                recordName.IndexOf("crafted", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Resolves the real PoE affix tier using the same ModsDat.recordsByTier
    /// path used by AdvancedTooltipPlus. The current mod record is matched
    /// against the tier records for its Group + AffixType, with the item's
    /// base-item tags and item level used to filter eligible tiers.
    /// </summary>
    private (int Tier, int TotalTiers) GetAuthoritativeModTier(Entity item, Mods mods, ItemMod mod)
    {
        if (item == null || mods == null || mod == null || string.IsNullOrEmpty(mod.RawName))
            return (0, 0);

        try
        {
            var record = GameController.Files.Mods.records[mod.RawName];
            if (record == null)
                return (0, 0);

            if (record.AffixType != ModType.Prefix && record.AffixType != ModType.Suffix)
                return (0, 0);

            // Crafted/implicit mods are not normal affix tiers.
            if (mods.ImplicitMods != null &&
                mods.ImplicitMods.Any(x => x != null && x.RawName == mod.RawName))
                return (0, 0);

            var baseItem = GameController.Files.BaseItemTypes.Translate(item.Path);
            if (baseItem == null)
                return ParseRecordTier(record);

            if (!GameController.Files.Mods.recordsByTier.TryGetValue(
                    Tuple.Create(record.Group, record.AffixType), out var allTiers))
                return ParseRecordTier(record);

            var recordLetters = new string(record.Key.Where(char.IsLetter).ToArray());
            var totalTiers = 0;
            var tier = 0;

            var tierRecords = allTiers
                .Where(x => x.Key.StartsWith(recordLetters, StringComparison.Ordinal))
                .ToList();

            foreach (var candidate in tierRecords)
            {
                if (candidate == null)
                    continue;

                var candidateLetters = new string(candidate.Key.Where(char.IsLetter).ToArray());
                if (!candidateLetters.SequenceEqual(recordLetters))
                    continue;

                var baseChance = -1;
                var defaultChance = 0;
                var tagChance = -1;
                var moreTagChance = -1;

                if (candidate.TagChances.TryGetValue(baseItem.ClassName.ToLower().Replace(' ', '_'), out var bc))
                    baseChance = bc;

                if (candidate.TagChances.TryGetValue("default", out var dc))
                    defaultChance = dc;

                foreach (var tag in baseItem.Tags)
                {
                    if (candidate.TagChances.TryGetValue(tag, out var chance))
                        tagChance = chance;
                }

                foreach (var tag in baseItem.MoreTagsFromPath)
                {
                    if (candidate.TagChances.TryGetValue(tag, out var chance))
                        moreTagChance = chance;
                }

                var eligible =
                    baseChance > 0 ||
                    (baseChance == -1 && tagChance > 0) ||
                    (baseChance == -1 && tagChance == -1 && moreTagChance > 0) ||
                    (baseChance == -1 && tagChance == -1 && moreTagChance == -1 && defaultChance > 0);

                if (!eligible)
                    continue;

                totalTiers++;

                if (candidate.Equals(record))
                    tier = totalTiers;
            }

            // Some records expose their tier directly; use that as a safe
            // fallback if recordsByTier could not resolve the ordinal.
            if (tier <= 0)
            {
                var parsed = ParseRecordTier(record);
                if (parsed.Tier > 0)
                    return (parsed.Tier, Math.Max(parsed.TotalTiers, totalTiers));
            }

            return (tier, totalTiers);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (int Tier, int TotalTiers) ParseRecordTier(ModsDat.ModRecord record)
    {
        if (record == null || string.IsNullOrEmpty(record.Tier))
            return (0, 0);

        var digits = new string(record.Tier.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var tier))
            return (tier, tier > 0 ? Math.Max(tier, 1) : 0);

        return (0, 0);
    }

    private List<string> BuildModRows(Entity item, Mods mods)
    {
        var result = new List<string>();

        if (mods?.ItemMods == null)
            return result;

        // The overlay's current item is supplied by the caller through the
        // per-render cache below. If unavailable, the normal mod text still
        // renders exactly as before.
        foreach (var mod in mods.ItemMods)
        {
            if (mod == null)
                continue;

            var values = GetMemberValues(mod);
            var rawName = GetMemberString(mod, "RawName");
            var text = GetReadableModText(mod, rawName, values);

            if (string.IsNullOrWhiteSpace(text))
                text = string.IsNullOrWhiteSpace(rawName) ? "Unknown modifier" : rawName;

            text = text.Replace("\r", " ").Replace("\n", " ");

            if (item != null)
            {
                // Crafted mods are not natural T1/T2/T3 affixes. Show a compact
                // "C" marker instead so the advanced display makes the distinction
                // immediately obvious.
                if (IsCraftedMod(mod))
                {
                    text += "  [C]";
                }
                else
                {
                    var tierInfo = GetAuthoritativeModTier(item, mods, mod);

                    // Only show a tier when the mod has multiple legitimate tiers.
                    // This avoids falsely labeling one-off/non-tiered affixes as T1.
                    if (tierInfo.Tier > 0 && tierInfo.TotalTiers > 1)
                        text += $"  [T{tierInfo.Tier}]";
                }
            }

            result.Add(text);
        }

        return result;
    }


    private static string GetReadableModText(object mod, string rawName, List<string> values)
    {
        // The debug build proved the user's ItemMod exposes Translation and
        // ModRecord. Prefer a real translated stat template whenever the
        // runtime provides one.
        var translated = TryGetTranslationText(mod);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            var formatted = ApplyModValues(translated, values);
            if (!string.IsNullOrWhiteSpace(formatted))
                return formatted;
        }

        // Fallback for common PoE stat keys. This also makes the overlay useful
        // if Translation is unavailable for a particular mod.
        var fallback = TranslateKnownMod(rawName, values);
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;

        // Last resort: use the human affix name, but always show the values so
        // we never silently lose information.
        var affix = GetMemberString(mod, "DisplayName", "Name");
        if (!string.IsNullOrWhiteSpace(affix))
            return values.Count > 0 ? $"{affix} [{string.Join(", ", values)}]" : affix;

        return rawName;
    }

    private static string TryGetTranslationText(object mod)
    {
        try
        {
            foreach (var owner in new[] { mod, GetMemberObject(mod, "ModRecord") })
            {
                if (owner == null)
                    continue;

                var direct = GetMemberObject(owner, "Translation");
                var text = ExtractTranslationString(direct, 0);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;

                text = GetMemberString(owner, "Translation", "Text", "Description", "Template");
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractTranslationString(object value, int depth)
    {
        if (value == null || depth > 2)
            return string.Empty;

        if (value is string s)
            return s;

        foreach (var name in new[] { "Text", "Translation", "Description", "Template", "String" })
        {
            var nested = GetMemberObject(value, name);
            if (nested is string ns && !string.IsNullOrWhiteSpace(ns))
                return ns;

            var recursive = ExtractTranslationString(nested, depth + 1);
            if (!string.IsNullOrWhiteSpace(recursive))
                return recursive;
        }

        return string.Empty;
    }

    private static string ApplyModValues(string template, List<string> values)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var result = template;
        for (var i = 0; i < values.Count; i++)
        {
            result = result.Replace("{" + i + "}", values[i], StringComparison.Ordinal);
        }

        // A few PoE translation templates use # placeholders. Replace them in
        // order without touching text that contains no placeholder.
        foreach (var value in values)
        {
            var hash = result.IndexOf('#');
            if (hash < 0)
                break;
            result = result.Remove(hash, 1).Insert(hash, value);
        }

        result = result.Trim();
        if (values.Count > 0 && result.IndexOf(values[0], StringComparison.Ordinal) < 0 &&
            result.IndexOf("#", StringComparison.Ordinal) < 0 &&
            result.IndexOf("{0}", StringComparison.Ordinal) < 0)
        {
            // Do not lose the numeric roll when a translation is only an affix
            // label (for example "OF THE THUNDERHEAD").
            result += values.Count == 1
                ? $" ({values[0]})"
                : $" ({string.Join(", ", values)})";
        }

        return result;
    }

    private static string TranslateKnownMod(string rawName, List<string> values)
    {
        if (string.IsNullOrWhiteSpace(rawName) || values == null || values.Count == 0)
            return string.Empty;

        var v0 = values[0];

        if (Contains(rawName, "MaximumLife") || Contains(rawName, "MaxLife"))
            return $"+{v0} to Maximum Life";
        if (Contains(rawName, "FireResist"))
            return $"+{v0}% to Fire Resistance";
        if (Contains(rawName, "ColdResist"))
            return $"+{v0}% to Cold Resistance";
        if (Contains(rawName, "LightningResist"))
            return $"+{v0}% to Lightning Resistance";
        if (Contains(rawName, "ChaosResist"))
            return $"+{v0}% to Chaos Resistance";
        if (Contains(rawName, "ResistAllElementsPct") || Contains(rawName, "AllResist"))
            return $"+{v0}% to all Elemental Resistances";
        if (Contains(rawName, "MovementVelocityPct") || Contains(rawName, "MovementSpeed"))
            return $"{v0}% increased Movement Speed";
        if (Contains(rawName, "Strength"))
            return $"+{v0} to Strength";
        if (Contains(rawName, "Dexterity"))
            return $"+{v0} to Dexterity";
        if (Contains(rawName, "Intelligence"))
            return $"+{v0} to Intelligence";
        if (Contains(rawName, "LocalIncreasedAttackSpeed"))
            return $"{v0}% increased Attack Speed";
        if (Contains(rawName, "IncreasedPhysicalDamagePercent"))
            return $"{v0}% increased Physical Damage";
        if (Contains(rawName, "AddedPhysicalDamage"))
        {
            var hi = values.Count > 1 ? values[1] : v0;
            return $"Adds {v0} to {hi} Physical Damage";
        }

        return string.Empty;
    }

    private static object GetMemberObject(object obj, string name)
    {
        if (obj == null)
            return null;

        try
        {
            var type = obj.GetType();
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
                return prop.GetValue(obj);

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(obj);
        }
        catch
        {
            return null;
        }
    }

    private string GetLootRatingReason(Entity item, Mods mods)
    {
        if (item == null || mods == null || mods.ItemRarity != ItemRarity.Rare)
            return string.Empty;

        var path = item.Path ?? string.Empty;

        if (IsBoot(path))
        {
            foreach (var mod in mods.ItemMods ?? new List<ItemMod>())
            {
                if (mod == null)
                    continue;

                var key = string.Join(" ", new[]
                {
                    GetMemberString(mod, "RawName"),
                    GetMemberString(mod, "Name"),
                    GetMemberString(mod, "DisplayName")
                }).ToLowerInvariant();

                if (key.Contains("movement") && key.Contains("speed"))
                    return string.Empty;
            }

            return "Movement Speed";
        }

        return string.Empty;
    }

    private List<string> BuildUserDefinedRatingDetails(Entity item, int rating)
    {
        var result = new List<string>();
        if (item == null || rating <= 0)
            return result;

        var mods = item.GetComponent<Mods>();
        if (mods == null)
            return result;

        var path = item.Path ?? string.Empty;
        var slot = GetStarSlot(path);
        if (slot == null)
            return result;

        var stats = ReadQualityStats(mods);

        // Keep ES consistent with the actual final post-mod defense value
        // displayed in the analyzer.
        try
        {
            var armour = item.GetComponent<Armour>();
            if (armour != null)
                stats.EnergyShield = CalculateFinalDefenses(item, mods, armour).EnergyShield;
        }
        catch
        {
        }

        foreach (var kv in slot.Thresholds)
        {
            var threshold = kv.Value;
            if (threshold <= 0)
                continue;

            var value = GetConfiguredStatValue(item, stats, kv.Key);
            if (value < threshold)
                continue;

            var label = kv.Key;
            var suffix = IsPercentageStat(label) ? "%" : "";

            // This is deliberately the user's configured threshold, not the
            // old hard-coded Good/Great/Excellent values. Include the amount
            // above the user's threshold so the result is immediately readable.
            var above = value - threshold;
            result.Add($"{label}: {value}{suffix} / {threshold}{suffix}  +{above}{suffix}");
        }

        return result;
    }

    private int GetConfiguredStatValue(Entity item, QualityStats s, string key)
    {
        switch (key)
        {
            case "Life": return s.Life;
            case "Cold Resistance": return s.ColdRes + s.AllRes;
            case "Fire Resistance": return s.FireRes + s.AllRes;
            case "Lightning Resistance": return s.LightningRes + s.AllRes;
            case "Chaos Resistance": return s.ChaosRes;
            case "Energy Shield": return s.EnergyShield;
            case "Spell Suppression": return s.SpellSuppression;
            case "Attributes": return Math.Max(s.Strength, Math.Max(s.Dexterity, s.Intelligence));
            case "Attack Speed": return s.AttackSpeed;
            case "Cast Speed": return s.CastSpeed;
            case "Crit Multiplier": return s.CritMultiplier;
            case "Movement Speed": return s.MoveSpeed;
            case "Mana": return s.Mana;
            case "Gem Levels": return s.GemLevels;
            case "Physical DPS":
                {
                    var mods = item?.GetComponent<Mods>();
                    return mods == null ? 0 : (int)Math.Round(CalculateWeaponDps(item, mods).Physical);
                }

            case "Total Weapon DPS":
            case "Weapon DPS":
                {
                    var mods = item?.GetComponent<Mods>();
                    return mods == null ? 0 : (int)Math.Round(CalculateWeaponDps(item, mods).Total);
                }

            default:
                return 0;
        }
    }

    private static bool IsPercentageStat(string key)
    {
        return key == "Cold Resistance" ||
               key == "Fire Resistance" ||
               key == "Lightning Resistance" ||
               key == "Chaos Resistance" ||
               key == "Spell Suppression" ||
               key == "Attack Speed" ||
               key == "Cast Speed" ||
               key == "Crit Multiplier" ||
               key == "Movement Speed";
    }

    private static string QualificationSuffix(int value, int good, int great, int excellent)
    {
        if (value >= excellent) return "  [EXCELLENT]";
        if (value >= great) return "  [GREAT]";
        if (value >= good) return "  [GOOD]";
        return string.Empty;
    }

    private static string RatingText(int rating)
    {
        return rating switch
        {
            3 => "★★★",
            2 => "★★",
            1 => "★",
            _ => "—"
        };
    }

    private static string GetItemDisplayName(Entity item)
    {
        if (item == null)
            return "Item";

        // RenderName was observed as EMPTY in the user's runtime, so only use
        // it if it contains meaningful text.
        var direct = GetMemberString(item, "RenderName");
        if (IsMeaningfulName(direct))
            return direct;

        // Try common metadata/name members without assuming a specific
        // ExileCore version.
        direct = GetMemberString(item, "Name", "DisplayName", "Text");
        if (IsMeaningfulName(direct))
            return direct;

        var metadata = GetMemberObject(item, "Metadata");
        direct = GetMemberString(metadata, "Name", "DisplayName", "BaseName", "Text");
        if (IsMeaningfulName(direct))
            return direct;

        // Last fallback: convert a metadata path such as GlovesStr8 into a
        // readable base-type label rather than showing EMPTY.
        return PrettyPathLeaf(item.Path);
    }

    private static bool IsMeaningfulName(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value.Trim(), "EMPTY", StringComparison.OrdinalIgnoreCase) &&
               !value.StartsWith("Metadata/", StringComparison.OrdinalIgnoreCase);
    }

    private static string PrettyPathLeaf(string path)
    {
        var leaf = GetPathLeaf(path);
        if (string.IsNullOrWhiteSpace(leaf) || string.Equals(leaf, "Item", StringComparison.OrdinalIgnoreCase))
            return "Item";

        // Remove the common base-type strength suffix used by PoE metadata.
        var name = System.Text.RegularExpressions.Regex.Replace(leaf, "(?:Str|Dex|Int|StrDex|StrInt|DexInt)\\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
        return name.Replace("  ", " ").Trim();
    }

    private static string GetPathLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Item";

        var slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length
            ? path.Substring(slash + 1)
            : path;
    }

    private static string GetMemberString(object obj, params string[] names)
    {
        if (obj == null)
            return string.Empty;

        foreach (var name in names)
        {
            try
            {
                var type = obj.GetType();
                var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    var value = prop.GetValue(obj);
                    if (value != null)
                        return value.ToString();
                }

                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    var value = field.GetValue(obj);
                    if (value != null)
                        return value.ToString();
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static List<string> GetMemberValues(object mod)
    {
        var result = new List<string>();
        if (mod == null)
            return result;

        try
        {
            var type = mod.GetType();
            var prop = type.GetProperty("Values", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var value = prop?.GetValue(mod);

            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var v in enumerable)
                    result.Add(v?.ToString() ?? string.Empty);
            }
        }
        catch
        {
        }

        return result;
    }

    private static uint ToImGuiColor(SharpDX.Color c)
    {
        return (uint)(c.R | (c.G << 8) | (c.B << 16) | (c.A << 24));
    }


    private static SharpDX.Color GetColor(ColorNode node, SharpDX.Color fallback)
    {
        if (node == null)
            return fallback;

        try { return node.Value; }
        catch { return fallback; }
    }
}
