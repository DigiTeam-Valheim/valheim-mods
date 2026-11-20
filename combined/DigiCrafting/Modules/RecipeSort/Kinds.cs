// Recipe Sort and Search: the kind of an item (weapons, armor, food, ...), from the game's item type. Pickaxes and
// fishing rods count as tools, mead bases as meads. The game calls bombs, snowballs and ember charges weapons too: weapons
// that stack are throwables. Weapons that do no damage (the Tankard, the Horn of Celebration) go to Other, except magic
// staffs (some do their work through what they summon). The order of the kinds is a setting.
// Within weapons and armor the type comes before the biome (no heading for it): helmets, chest pieces, leggings, gloves,
// capes; swords, axes, maces, knives, spears, atgeirs, fists, bows, crossbows, staffs (one-handed before two-handed).
using System;

namespace RecipeSort
{
    internal static class Kinds
    {
        internal const string DefaultOrder = "Weapons, Throwables, Shields, Ammo, Armor, Utility, Tools, Food, Meads, Materials, Other";
        // Before Crafting and Automation 1.0.16 (no throwables yet): a setting still at this gets the new default once.
        internal const string OldDefaultOrder = "Weapons, Shields, Ammo, Armor, Utility, Tools, Food, Meads, Materials, Other";
        private const int Weapons = 0, Shields = 1, Ammo = 2, Armor = 3, Utility = 4, Tools = 5, Food = 6, Meads = 7, Materials = 8, Other = 9,
            Throwables = 10;
        private static readonly string[] Names =
            { "Weapons", "Shields", "Ammo", "Armor", "Utility", "Tools", "Food", "Meads", "Materials", "Other", "Throwables" };
        private static readonly int[] RankOf = new int[Names.Length];
        private static string _order;

        internal static int Of(ItemDrop item)
        {
            var shared = item?.m_itemData?.m_shared;
            if (shared == null) return Other;
            switch (shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                    if (shared.m_skillType == Skills.SkillType.Pickaxes || shared.m_skillType == Skills.SkillType.Fishing) return Tools;
                    if (shared.m_maxStackSize > 1) return Throwables;
                    if (shared.m_damages.GetTotalDamage() <= 0f && shared.m_skillType != Skills.SkillType.ElementalMagic &&
                        shared.m_skillType != Skills.SkillType.BloodMagic) return Other;
                    return Weapons;
                case ItemDrop.ItemData.ItemType.Shield:
                    return Shields;
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                    return Ammo;
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Hands:
                    return Armor;
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                    return Utility;
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                    return Tools;
                case ItemDrop.ItemData.ItemType.Consumable:
                    return shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f ? Food : Meads;
                case ItemDrop.ItemData.ItemType.Material:
                    return Tiers.IsMeadBase(item) ? Meads : Materials;
                default:
                    return Other;
            }
        }

        // The weapon skills in list order; others after them.
        private static readonly Skills.SkillType[] WeaponOrder =
        {
            Skills.SkillType.Swords, Skills.SkillType.Axes, Skills.SkillType.Clubs, Skills.SkillType.Knives, Skills.SkillType.Spears,
            Skills.SkillType.Polearms, Skills.SkillType.Unarmed, Skills.SkillType.Bows, Skills.SkillType.Crossbows,
            Skills.SkillType.ElementalMagic, Skills.SkillType.BloodMagic,
        };

        // The type within a kind (kind as Of gives it): armor by slot, weapons by skill and then hands. 0 for other kinds.
        internal static int TypeOf(ItemDrop item, int kind)
        {
            var shared = item?.m_itemData?.m_shared;
            if (shared == null) return 0;
            if (kind == Armor)
            {
                switch (shared.m_itemType)
                {
                    case ItemDrop.ItemData.ItemType.Helmet: return 0;
                    case ItemDrop.ItemData.ItemType.Chest: return 1;
                    case ItemDrop.ItemData.ItemType.Legs: return 2;
                    case ItemDrop.ItemData.ItemType.Hands: return 3;
                    case ItemDrop.ItemData.ItemType.Shoulder: return 4;
                    default: return 5;
                }
            }
            if (kind != Weapons) return 0;
            var rank = Array.IndexOf(WeaponOrder, shared.m_skillType);
            if (rank < 0) rank = WeaponOrder.Length;
            var twoHanded = shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                            shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
            return rank * 2 + (twoHanded ? 1 : 0);
        }

        internal static string Name(int kind) => kind >= 0 && kind < Names.Length ? Names[kind] : "";

        // Place in the list: kinds named in the setting come first in that order, the rest after in the default order.
        internal static int Rank(int kind)
        {
            var order = Plugin.KindOrder.Value ?? "";
            if (order != _order)
            {
                _order = order;
                for (var i = 0; i < RankOf.Length; i++) RankOf[i] = 100 + i;
                var next = 0;
                foreach (var word in order.Split(','))
                {
                    var i = Array.FindIndex(Names, n => string.Equals(n, word.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (i >= 0 && RankOf[i] >= 100) RankOf[i] = next++;
                }
            }
            return kind >= 0 && kind < RankOf.Length ? RankOf[kind] : 200;
        }
    }
}
