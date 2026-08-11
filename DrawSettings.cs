using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore.Shared.Nodes;
using ImGuiNET;

namespace InventoryItemAnalyzer;

public partial class InventoryItemAnalyzer
{
    public override void DrawSettings()
    {
        ImGui.TextColored(new Vector4(0.20f, 0.75f, 1.00f, 1f), "INVENTORY ITEM ANALYZER");
        ImGui.TextDisabled("Detailed item inspection, tiers, defenses and loot rating.");
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##analyzer_main_tabs"))
            return;

        if (ImGui.BeginTabItem("Analyzer"))
        {
            DrawAnalyzerSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Custom Stars"))
        {
            DrawCustomStarsSettings();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawAnalyzerSettings()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("ITEM ANALYZER");
        ImGui.Separator();
        Toggle("Enable Plugin", Settings.Enable);
        Toggle("Item Info Overlay", Settings.ShowItemInfo);
        Toggle("Always Show Item Info", Settings.AlwaysShowItemInfo);

        ImGui.Spacing();
        ImGui.TextDisabled("APPEARANCE");
        ImGui.Separator();
        Slider("Width", Settings.ItemInfoWidth);
        ColorNodeEditor("Background", Settings.ItemInfoBackground);
        ColorNodeEditor("Border", Settings.ItemInfoBorder);
        ColorNodeEditor("Tier Color", Settings.ItemInfoTierColor);

        ImGui.Spacing();
        ImGui.TextDisabled("KEYBIND");
        ImGui.Separator();
        DrawFullAnalyzerKeybind();

        ImGui.Spacing();
        Toggle("DEBUG Mode", Settings.ItemInfoDebugMode);
        ImGui.TextDisabled("The inspection key is configured in the normal plugin settings.");
    }
    private bool _capturingFullAnalyzerKey;
    private static readonly string[] _keyNames = BuildKeyNames();

    private void DrawFullAnalyzerKeybind()
    {
        ImGui.Text("Hold Key for Full Analyzer");
        ImGui.SameLine();

        var currentKey = (int)Settings.ItemInfoHotkey.Value;
        var label = GetKeyDisplayName(currentKey);

        if (ImGui.Button(_capturingFullAnalyzerKey ? "PRESS A KEY..." : label,
            new Vector2(220f, 0f)))
        {
            _capturingFullAnalyzerKey = true;
        }

        if (_capturingFullAnalyzerKey)
        {
            for (var key = 1; key < 256; key++)
            {
                if ((GetAsyncKeyState(key) & 0x8000) == 0)
                    continue;

                // Ignore mouse buttons and modifier combinations here; the
                // analyzer uses the same virtual-key code path as its runtime
                // visibility check.
                if (key == 1 || key == 2 || key == 4 || key == 5 || key == 6)
                    continue;

                Settings.ItemInfoHotkey.Value = (System.Windows.Forms.Keys)key;
                _capturingFullAnalyzerKey = false;
                break;
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Hold this key to temporarily show the full analyzer.");
    }

    private static string GetKeyDisplayName(int key)
    {
        if (key <= 0)
            return "NONE";

        try
        {
            var name = Enum.GetName(typeof(System.Windows.Forms.Keys), key);
            if (!string.IsNullOrWhiteSpace(name))
                return name.Replace("ControlKey", "CTRL");
        }
        catch
        {
        }

        return $"VK {key}";
    }

    private static string[] BuildKeyNames()
    {
        var result = new string[256];
        for (var i = 0; i < result.Length; i++)
            result[i] = GetKeyDisplayName(i);
        return result;
    }

    private void DrawCustomStarsSettings()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("Set how many of your configured valuable stats an item needs to reach each star level. A stat threshold of 0 means it does not count.");
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##iro_star_tabs"))
            return;

        DrawStarTab("Helmet", 0);
        DrawStarTab("Body Armour", 1);
        DrawStarTab("Gloves", 2);
        DrawStarTab("Boots", 3);
        DrawStarTab("Shield", 4);
        DrawStarTab("Belt", 5);
        DrawStarTab("Ring", 6);
        DrawStarTab("Amulet", 7);
        DrawStarTab("Quiver", 8);
        DrawStarTab("Weapon", 9);

        ImGui.EndTabBar();
    }

    private void DrawStarTab(string name, int index)
    {
        if (!ImGui.BeginTabItem(name + $"##star_slot_{index}"))
            return;

        var slot = BuildStarSlot(index);

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.20f, 0.75f, 1f, 1f),
            name.ToUpperInvariant());
        ImGui.SameLine();
        ImGui.TextDisabled("- USER-DEFINED STAR RULES");

        if (name == "Weapon")
        {
            ImGui.Spacing();
            ImGui.TextDisabled("WEAPON QUALIFIER");
            ImGui.Separator();

            var dpsMode = Settings.Star_Weapon_DpsMode.Value;
            ImGui.Text("Qualify weapons by:");
            ImGui.SameLine();
            if (ImGui.RadioButton("Physical DPS", dpsMode == 0))
            {
                Settings.Star_Weapon_DpsMode.Value = 0;
                dpsMode = 0;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Total Weapon DPS", dpsMode == 1))
            {
                Settings.Star_Weapon_DpsMode.Value = 1;
                dpsMode = 1;
            }

            ImGui.TextDisabled(
                dpsMode == 0
                    ? "Physical damage only."
                    : "Physical + elemental damage.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("STAR THRESHOLDS");
        ImGui.Separator();

        StarThresholdSlider(slot.Name, 1, slot.OneStar);
        StarThresholdSlider(slot.Name, 2, slot.TwoStar);
        StarThresholdSlider(slot.Name, 3, slot.ThreeStar);

        ImGui.Spacing();
        ImGui.TextDisabled("VALUABLE STATS");
        ImGui.Separator();

        foreach (var stat in slot.Stats)
        {
            var displayName = stat.Name;
            if (name == "Weapon" && stat.Name == "Weapon DPS")
                displayName = Settings.Star_Weapon_DpsMode.Value == 0
                    ? "Physical DPS"
                    : "Total Weapon DPS";

            Slider(displayName, stat.Node);
if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Set to 0 to ignore this stat.");
                ImGui.Separator();
                ImGui.TextDisabled("Threshold = minimum qualifying roll.");
                ImGui.EndTooltip();
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("The item receives the highest star tier whose required-stat count is met.");

        ImGui.Spacing();
        if (ImGui.Button($"Reset {name}"))
            ResetStarSlot(index);

        ImGui.EndTabItem();
    }


    private static readonly Dictionary<string, int> DefaultStarValues = new()
    {
        ["Star_Helmet_GoodRequired"] = 2,
        ["Star_Helmet_GreatRequired"] = 3,
        ["Star_Helmet_ExcellentRequired"] = 4,
        ["Star_Helmet_Life"] = 80,
        ["Star_Helmet_ColdResistance"] = 30,
        ["Star_Helmet_FireResistance"] = 30,
        ["Star_Helmet_LightningResistance"] = 30,
        ["Star_Helmet_ChaosResistance"] = 20,
        ["Star_Helmet_EnergyShield"] = 100,
        ["Star_Helmet_SpellSuppression"] = 12,
        ["Star_Helmet_Attributes"] = 30,
        ["Star_BodyArmour_GoodRequired"] = 2,
        ["Star_BodyArmour_GreatRequired"] = 3,
        ["Star_BodyArmour_ExcellentRequired"] = 4,
        ["Star_BodyArmour_Life"] = 80,
        ["Star_BodyArmour_ColdResistance"] = 30,
        ["Star_BodyArmour_FireResistance"] = 30,
        ["Star_BodyArmour_LightningResistance"] = 30,
        ["Star_BodyArmour_ChaosResistance"] = 20,
        ["Star_BodyArmour_EnergyShield"] = 100,
        ["Star_BodyArmour_SpellSuppression"] = 12,
        ["Star_BodyArmour_Attributes"] = 30,
        ["Star_Gloves_GoodRequired"] = 2,
        ["Star_Gloves_GreatRequired"] = 3,
        ["Star_Gloves_ExcellentRequired"] = 4,
        ["Star_Gloves_Life"] = 80,
        ["Star_Gloves_ColdResistance"] = 30,
        ["Star_Gloves_FireResistance"] = 30,
        ["Star_Gloves_LightningResistance"] = 30,
        ["Star_Gloves_ChaosResistance"] = 20,
        ["Star_Gloves_SpellSuppression"] = 12,
        ["Star_Gloves_AttackSpeed"] = 10,
        ["Star_Gloves_CastSpeed"] = 10,
        ["Star_Gloves_CritMultiplier"] = 20,
        ["Star_Gloves_Attributes"] = 30,
        ["Star_Boots_GoodRequired"] = 2,
        ["Star_Boots_GreatRequired"] = 3,
        ["Star_Boots_ExcellentRequired"] = 4,
        ["Star_Boots_Life"] = 80,
        ["Star_Boots_ColdResistance"] = 30,
        ["Star_Boots_FireResistance"] = 30,
        ["Star_Boots_LightningResistance"] = 30,
        ["Star_Boots_ChaosResistance"] = 20,
        ["Star_Boots_SpellSuppression"] = 12,
        ["Star_Boots_Attributes"] = 30,
        ["Star_Boots_MovementSpeed"] = 30,
        ["Star_Belt_GoodRequired"] = 2,
        ["Star_Belt_GreatRequired"] = 3,
        ["Star_Belt_ExcellentRequired"] = 4,
        ["Star_Belt_Life"] = 80,
        ["Star_Belt_ColdResistance"] = 30,
        ["Star_Belt_FireResistance"] = 30,
        ["Star_Belt_LightningResistance"] = 30,
        ["Star_Belt_ChaosResistance"] = 20,
        ["Star_Belt_Attributes"] = 30,
        ["Star_Belt_Mana"] = 50,
        ["Star_Ring_GoodRequired"] = 2,
        ["Star_Ring_GreatRequired"] = 3,
        ["Star_Ring_ExcellentRequired"] = 4,
        ["Star_Ring_Life"] = 80,
        ["Star_Ring_ColdResistance"] = 30,
        ["Star_Ring_FireResistance"] = 30,
        ["Star_Ring_LightningResistance"] = 30,
        ["Star_Ring_ChaosResistance"] = 20,
        ["Star_Ring_Attributes"] = 30,
        ["Star_Ring_Mana"] = 50,
        ["Star_Ring_AttackSpeed"] = 10,
        ["Star_Ring_CastSpeed"] = 10,
        ["Star_Ring_CritMultiplier"] = 20,
        ["Star_Ring_Accuracy"] = 200,
        ["Star_Amulet_GoodRequired"] = 2,
        ["Star_Amulet_GreatRequired"] = 3,
        ["Star_Amulet_ExcellentRequired"] = 4,
        ["Star_Amulet_Life"] = 80,
        ["Star_Amulet_ColdResistance"] = 30,
        ["Star_Amulet_FireResistance"] = 30,
        ["Star_Amulet_LightningResistance"] = 30,
        ["Star_Amulet_ChaosResistance"] = 20,
        ["Star_Amulet_Attributes"] = 30,
        ["Star_Amulet_EnergyShield"] = 100,
        ["Star_Amulet_GemLevels"] = 1,
        ["Star_Amulet_CritMultiplier"] = 20,
        ["Star_Shield_GoodRequired"] = 2,
        ["Star_Shield_GreatRequired"] = 3,
        ["Star_Shield_ExcellentRequired"] = 4,
        ["Star_Shield_Life"] = 80,
        ["Star_Shield_ColdResistance"] = 30,
        ["Star_Shield_FireResistance"] = 30,
        ["Star_Shield_LightningResistance"] = 30,
        ["Star_Shield_ChaosResistance"] = 20,
        ["Star_Shield_EnergyShield"] = 100,
        ["Star_Shield_SpellSuppression"] = 12,
        ["Star_Shield_Attributes"] = 30,
        ["Star_Quiver_GoodRequired"] = 2,
        ["Star_Quiver_GreatRequired"] = 3,
        ["Star_Quiver_ExcellentRequired"] = 4,
        ["Star_Quiver_Life"] = 80,
        ["Star_Quiver_ColdResistance"] = 30,
        ["Star_Quiver_FireResistance"] = 30,
        ["Star_Quiver_LightningResistance"] = 30,
        ["Star_Quiver_ChaosResistance"] = 20,
        ["Star_Quiver_AttackSpeed"] = 10,
        ["Star_Quiver_CritMultiplier"] = 20,
        ["Star_Quiver_Attributes"] = 30,
        ["Star_Weapon_GoodRequired"] = 2,
        ["Star_Weapon_GreatRequired"] = 3,
        ["Star_Weapon_ExcellentRequired"] = 4,
        ["Star_Weapon_WeaponDps"] = 200,
        ["Star_Weapon_AttackSpeed"] = 10,
        ["Star_Weapon_CritMultiplier"] = 20,
        ["Star_Weapon_ColdResistance"] = 30,
        ["Star_Weapon_FireResistance"] = 30,
        ["Star_Weapon_LightningResistance"] = 30,
        ["Star_Weapon_Attributes"] = 30,
        ["Star_Weapon_GemLevels"] = 1
    };

    private void ResetStarSlot(int index)
    {
        string[] slotNames =
        {
            "Helmet", "BodyArmour", "Gloves", "Boots", "Shield",
            "Belt", "Ring", "Amulet", "Quiver", "Weapon"
        };

        if (index < 0 || index >= slotNames.Length)
            return;

        string prefix = "Star_" + slotNames[index] + "_";

        foreach (var property in typeof(Settings).GetProperties())
        {
            if (!property.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (property.GetValue(Settings) is RangeNode<int> node &&
                DefaultStarValues.TryGetValue(property.Name, out var defaultValue))
            {
                node.Value = defaultValue;
            }
        }
    }

    private StarSlotView BuildStarSlot(int index)
    {
        switch (index)
        {
            case 0:
                return Slot("Helmet",
                    Settings.Star_Helmet_GoodRequired,
                    Settings.Star_Helmet_GreatRequired,
                    Settings.Star_Helmet_ExcellentRequired,
                    ("Life", Settings.Star_Helmet_Life),
                    ("Cold Resistance", Settings.Star_Helmet_ColdResistance),
                    ("Fire Resistance", Settings.Star_Helmet_FireResistance),
                    ("Lightning Resistance", Settings.Star_Helmet_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Helmet_ChaosResistance),
                    ("Energy Shield", Settings.Star_Helmet_EnergyShield),
                    ("Spell Suppression", Settings.Star_Helmet_SpellSuppression),
                    ("Attributes", Settings.Star_Helmet_Attributes));

            case 1:
                return Slot("Body Armour",
                    Settings.Star_BodyArmour_GoodRequired,
                    Settings.Star_BodyArmour_GreatRequired,
                    Settings.Star_BodyArmour_ExcellentRequired,
                    ("Life", Settings.Star_BodyArmour_Life),
                    ("Cold Resistance", Settings.Star_BodyArmour_ColdResistance),
                    ("Fire Resistance", Settings.Star_BodyArmour_FireResistance),
                    ("Lightning Resistance", Settings.Star_BodyArmour_LightningResistance),
                    ("Chaos Resistance", Settings.Star_BodyArmour_ChaosResistance),
                    ("Energy Shield", Settings.Star_BodyArmour_EnergyShield),
                    ("Spell Suppression", Settings.Star_BodyArmour_SpellSuppression),
                    ("Attributes", Settings.Star_BodyArmour_Attributes));

            case 2:
                return Slot("Gloves",
                    Settings.Star_Gloves_GoodRequired,
                    Settings.Star_Gloves_GreatRequired,
                    Settings.Star_Gloves_ExcellentRequired,
                    ("Life", Settings.Star_Gloves_Life),
                    ("Cold Resistance", Settings.Star_Gloves_ColdResistance),
                    ("Fire Resistance", Settings.Star_Gloves_FireResistance),
                    ("Lightning Resistance", Settings.Star_Gloves_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Gloves_ChaosResistance),
                    ("Spell Suppression", Settings.Star_Gloves_SpellSuppression),
                    ("Attack Speed", Settings.Star_Gloves_AttackSpeed),
                    ("Cast Speed", Settings.Star_Gloves_CastSpeed),
                    ("Crit Multiplier", Settings.Star_Gloves_CritMultiplier),
                    ("Attributes", Settings.Star_Gloves_Attributes));

            case 3:
                return Slot("Boots",
                    Settings.Star_Boots_GoodRequired,
                    Settings.Star_Boots_GreatRequired,
                    Settings.Star_Boots_ExcellentRequired,
                    ("Life", Settings.Star_Boots_Life),
                    ("Cold Resistance", Settings.Star_Boots_ColdResistance),
                    ("Fire Resistance", Settings.Star_Boots_FireResistance),
                    ("Lightning Resistance", Settings.Star_Boots_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Boots_ChaosResistance),
                    ("Spell Suppression", Settings.Star_Boots_SpellSuppression),
                    ("Attributes", Settings.Star_Boots_Attributes),
                    ("Movement Speed", Settings.Star_Boots_MovementSpeed));

            case 4:
                return Slot("Shield",
                    Settings.Star_Shield_GoodRequired,
                    Settings.Star_Shield_GreatRequired,
                    Settings.Star_Shield_ExcellentRequired,
                    ("Life", Settings.Star_Shield_Life),
                    ("Cold Resistance", Settings.Star_Shield_ColdResistance),
                    ("Fire Resistance", Settings.Star_Shield_FireResistance),
                    ("Lightning Resistance", Settings.Star_Shield_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Shield_ChaosResistance),
                    ("Energy Shield", Settings.Star_Shield_EnergyShield),
                    ("Spell Suppression", Settings.Star_Shield_SpellSuppression),
                    ("Attributes", Settings.Star_Shield_Attributes));

            case 5:
                return Slot("Belt",
                    Settings.Star_Belt_GoodRequired,
                    Settings.Star_Belt_GreatRequired,
                    Settings.Star_Belt_ExcellentRequired,
                    ("Life", Settings.Star_Belt_Life),
                    ("Cold Resistance", Settings.Star_Belt_ColdResistance),
                    ("Fire Resistance", Settings.Star_Belt_FireResistance),
                    ("Lightning Resistance", Settings.Star_Belt_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Belt_ChaosResistance),
                    ("Attributes", Settings.Star_Belt_Attributes),
                    ("Mana", Settings.Star_Belt_Mana));

            case 6:
                return Slot("Ring",
                    Settings.Star_Ring_GoodRequired,
                    Settings.Star_Ring_GreatRequired,
                    Settings.Star_Ring_ExcellentRequired,
                    ("Life", Settings.Star_Ring_Life),
                    ("Cold Resistance", Settings.Star_Ring_ColdResistance),
                    ("Fire Resistance", Settings.Star_Ring_FireResistance),
                    ("Lightning Resistance", Settings.Star_Ring_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Ring_ChaosResistance),
                    ("Attributes", Settings.Star_Ring_Attributes),
                    ("Mana", Settings.Star_Ring_Mana),
                    ("Attack Speed", Settings.Star_Ring_AttackSpeed),
                    ("Cast Speed", Settings.Star_Ring_CastSpeed),
                    ("Crit Multiplier", Settings.Star_Ring_CritMultiplier),
                    ("Accuracy", Settings.Star_Ring_Accuracy));

            case 7:
                return Slot("Amulet",
                    Settings.Star_Amulet_GoodRequired,
                    Settings.Star_Amulet_GreatRequired,
                    Settings.Star_Amulet_ExcellentRequired,
                    ("Life", Settings.Star_Amulet_Life),
                    ("Cold Resistance", Settings.Star_Amulet_ColdResistance),
                    ("Fire Resistance", Settings.Star_Amulet_FireResistance),
                    ("Lightning Resistance", Settings.Star_Amulet_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Amulet_ChaosResistance),
                    ("Attributes", Settings.Star_Amulet_Attributes),
                    ("Energy Shield", Settings.Star_Amulet_EnergyShield),
                    ("Gem Levels", Settings.Star_Amulet_GemLevels),
                    ("Crit Multiplier", Settings.Star_Amulet_CritMultiplier));

            case 8:
                return Slot("Quiver",
                    Settings.Star_Quiver_GoodRequired,
                    Settings.Star_Quiver_GreatRequired,
                    Settings.Star_Quiver_ExcellentRequired,
                    ("Life", Settings.Star_Quiver_Life),
                    ("Cold Resistance", Settings.Star_Quiver_ColdResistance),
                    ("Fire Resistance", Settings.Star_Quiver_FireResistance),
                    ("Lightning Resistance", Settings.Star_Quiver_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Quiver_ChaosResistance),
                    ("Attack Speed", Settings.Star_Quiver_AttackSpeed),
                    ("Crit Multiplier", Settings.Star_Quiver_CritMultiplier),
                    ("Attributes", Settings.Star_Quiver_Attributes));

            default:
                return Slot("Weapon",
                    Settings.Star_Weapon_GoodRequired,
                    Settings.Star_Weapon_GreatRequired,
                    Settings.Star_Weapon_ExcellentRequired,
                    ("Weapon DPS", Settings.Star_Weapon_WeaponDps),
                    ("Attack Speed", Settings.Star_Weapon_AttackSpeed),
                    ("Crit Multiplier", Settings.Star_Weapon_CritMultiplier),
                    ("Cold Resistance", Settings.Star_Weapon_ColdResistance),
                    ("Fire Resistance", Settings.Star_Weapon_FireResistance),
                    ("Lightning Resistance", Settings.Star_Weapon_LightningResistance),
                    ("Attributes", Settings.Star_Weapon_Attributes),
                    ("Gem Levels", Settings.Star_Weapon_GemLevels));
        }
    }

    private static StarSlotView Slot(string name,
        RangeNode<int> one, RangeNode<int> two, RangeNode<int> three,
        params (string Name, RangeNode<int> Node)[] stats)
    {
        var result = new StarSlotView
        {
            Name = name,
            OneStar = one,
            TwoStar = two,
            ThreeStar = three
        };

        foreach (var stat in stats)
            result.Stats.Add(new StarStatView { Name = stat.Name, Node = stat.Node });

        return result;
    }

    private static void Toggle(string label, ToggleNode node)
    {
        bool value = node.Value;
        if (ImGui.Checkbox(label, ref value))
            node.Value = value;
    }

    private static void StarThresholdSlider(string slotName, int stars, RangeNode<int> node)
    {
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        const float radius = 7f;
        const float gap = 4f;
        var centerY = start.Y + 9f;

        for (var i = 0; i < stars; i++)
        {
            var cx = start.X + radius + i * (radius * 2f + gap);
            DrawStar(draw, new Vector2(cx, centerY), radius, Col(1.00f, 0.78f, 0.20f));
        }

        ImGui.Dummy(new Vector2(stars * (radius * 2f + gap), 18f));
        ImGui.SameLine();
        Slider($"Stats Needed##{slotName}_{stars}", node);
    }

    private static void DrawStar(ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        var points = new Vector2[10];
        for (var i = 0; i < 10; i++)
        {
            var angle = -MathF.PI / 2f + i * MathF.PI / 5f;
            var r = (i % 2 == 0) ? radius : radius * 0.42f;
            points[i] = new Vector2(
                center.X + MathF.Cos(angle) * r,
                center.Y + MathF.Sin(angle) * r);
        }

        draw.AddConvexPolyFilled(ref points[0], points.Length, color);
    }

    private static uint Col(float r, float g, float b, float a = 1f)
    {
        byte R = (byte)Math.Clamp((int)(r * 255f), 0, 255);
        byte G = (byte)Math.Clamp((int)(g * 255f), 0, 255);
        byte B = (byte)Math.Clamp((int)(b * 255f), 0, 255);
        byte A = (byte)Math.Clamp((int)(a * 255f), 0, 255);
        return (uint)(R | ((uint)G << 8) | ((uint)B << 16) | ((uint)A << 24));
    }

    private static void Slider(string label, RangeNode<int> node)
    {
        int value = node.Value;
        ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X * 0.55f));
        if (ImGui.SliderInt(label, ref value, node.Min, node.Max))
            node.Value = value;
    }

    private static void ColorNodeEditor(string label, ColorNode node)
    {
        var c = node.Value;
        var v = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        if (ImGui.ColorEdit4(label, ref v, ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.AlphaBar))
        {
            c.R = (byte)Math.Clamp((int)(v.X * 255f), 0, 255);
            c.G = (byte)Math.Clamp((int)(v.Y * 255f), 0, 255);
            c.B = (byte)Math.Clamp((int)(v.Z * 255f), 0, 255);
            c.A = (byte)Math.Clamp((int)(v.W * 255f), 0, 255);
            node.Value = c;
        }
    }

    private sealed class StarSlotView
    {
        public string Name { get; set; } = string.Empty;
        public RangeNode<int> OneStar { get; set; }
        public RangeNode<int> TwoStar { get; set; }
        public RangeNode<int> ThreeStar { get; set; }
        public List<StarStatView> Stats { get; } = new();
    }

    private sealed class StarStatView
    {
        public string Name { get; set; } = string.Empty;
        public RangeNode<int> Node { get; set; }
    }


}
