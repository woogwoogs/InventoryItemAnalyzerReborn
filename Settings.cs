using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

using Color = SharpDX.Color;

namespace InventoryItemAnalyzer;

public class Settings : ISettings
{
    [Menu("Enable Plugin")]
    public ToggleNode Enable { get; set; } = new(true);
    [Menu("Star Color")]
    public ColorNode StarColor { get; set; } = new(Color.Gold);

    [Menu("Item Info Overlay")]
    public ToggleNode ShowItemInfo { get; set; } = new(true);

    [Menu("Always Show Item Info")]
    public ToggleNode AlwaysShowItemInfo { get; set; } = new(true);

    [Menu("Item Info - Hold Key for Full Analyzer")]
    public HotkeyNode ItemInfoHotkey { get; set; } = new(Keys.Menu);






    // ============================================================
    // CUSTOM STAR SYSTEM - SLOT TABS
    // 0 on a stat slider means "do not count this stat".
    // ============================================================
    public RangeNode<int> Star_Helmet_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Helmet_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Helmet_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Helmet_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Helmet_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Helmet_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Helmet_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Helmet_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Helmet_EnergyShield { get; set; } = new(100, 0, 1000);
    public RangeNode<int> Star_Helmet_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_Helmet_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_BodyArmour_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_BodyArmour_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_BodyArmour_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_BodyArmour_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_BodyArmour_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_BodyArmour_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_BodyArmour_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_BodyArmour_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_BodyArmour_EnergyShield { get; set; } = new(100, 0, 1000);
    public RangeNode<int> Star_BodyArmour_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_BodyArmour_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Gloves_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Gloves_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Gloves_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Gloves_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Gloves_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Gloves_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Gloves_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Gloves_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Gloves_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_Gloves_AttackSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Gloves_CastSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Gloves_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Gloves_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Boots_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Boots_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Boots_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Boots_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Boots_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Boots_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Boots_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Boots_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Boots_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_Boots_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Boots_MovementSpeed { get; set; } = new(30, 0, 50);
    public RangeNode<int> Star_Belt_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Belt_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Belt_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Belt_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Belt_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Belt_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Belt_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Belt_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Belt_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Belt_Mana { get; set; } = new(50, 0, 300);
    public RangeNode<int> Star_Ring_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Ring_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Ring_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Ring_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Ring_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Ring_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Ring_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Ring_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Ring_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Ring_Mana { get; set; } = new(50, 0, 300);
    public RangeNode<int> Star_Ring_AttackSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Ring_CastSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Ring_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Ring_Accuracy { get; set; } = new(200, 0, 1000);
    public RangeNode<int> Star_Amulet_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Amulet_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Amulet_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Amulet_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Amulet_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Amulet_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Amulet_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Amulet_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Amulet_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Amulet_EnergyShield { get; set; } = new(100, 0, 1000);
    public RangeNode<int> Star_Amulet_GemLevels { get; set; } = new(1, 0, 5);
    public RangeNode<int> Star_Amulet_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Shield_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Shield_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Shield_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Shield_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Shield_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Shield_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Shield_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Shield_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Shield_EnergyShield { get; set; } = new(100, 0, 1000);
    public RangeNode<int> Star_Shield_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_Shield_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Quiver_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Quiver_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Quiver_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Quiver_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Quiver_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Quiver_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Quiver_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Quiver_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Quiver_AttackSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Quiver_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Quiver_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Weapon_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Weapon_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Weapon_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Weapon_WeaponDps { get; set; } = new(200, 0, 1000);

    // 0 = Physical DPS, 1 = Total Weapon DPS.
    public RangeNode<int> Star_Weapon_DpsMode { get; set; } = new(1, 0, 1);
    public RangeNode<int> Star_Weapon_AttackSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Weapon_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Weapon_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Weapon_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Weapon_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Weapon_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Weapon_GemLevels { get; set; } = new(1, 0, 5);


    [Menu("Item Info - Compact Mode")]
    public ToggleNode ItemInfoCompactMode { get; set; } = new(true);


    [Menu("Item Info - Width")]
    public RangeNode<int> ItemInfoWidth { get; set; } = new(310, 220, 600);

    [Menu("Item Info - Background")]
    public ColorNode ItemInfoBackground { get; set; } = new(new Color(15, 15, 20, 245));

    [Menu("Item Info - Border")]
    public ColorNode ItemInfoBorder { get; set; } = new(new Color(110, 110, 125, 255));

    [Menu("Item Info - Tier Color")]
    public ColorNode ItemInfoTierColor { get; set; } = new(Color.Gold);

    [Menu("Item Info - DEBUG Mode (temporary)")]
    public ToggleNode ItemInfoDebugMode { get; set; } = new(false);

}

