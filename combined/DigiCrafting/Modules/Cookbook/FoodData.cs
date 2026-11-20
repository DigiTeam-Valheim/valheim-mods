// Every food in the game (vanilla and modded): anything with health, stamina or eitr when eaten, and every mead:
// anything a fermenter makes, listed apart with what it does instead of food values. How it is made comes
// from the game's own data: crafting recipes (cauldron, ...), cooking stations (fire, iron grill, oven, ...) and
// buildings that turn one item into another (windmill).
// Food that is made none of these ways is "Gathered" (berries, mushrooms, honey, ...).
// Ingredients that are made from others (dough, bread, flour, cooked meat, ...) are followed by what they are made from,
// all the way down: "Honey 4, Bread = Bread dough = Barley flour 10 = Barley". Plain ingredients come first and made ones last, so
// each "=" reads "made from". A line before a made ingredient with neighbours at its step shows what belongs to what:
// a full line between the recipe's own ingredients, a short one inside one of them:
// "Lingonberries 2 | Oats 2 = Oat seeds | Oat milk = Ice 5 : Oats 5 = Oat seeds".
using System.Collections.Generic;
using UnityEngine;

namespace Cookbook
{
    internal enum FoodType { Health, Stamina, Eitr, Balanced }

    internal sealed class Ingredient
    {
        public string Name;
        public Sprite Icon;
        public int Amount;
        public ItemDrop Item;
        public bool Before; // made beforehand from the ingredients after it (e.g. the unbaked pie that goes into the oven)
        public bool Apart; // a made ingredient with neighbours at its step: a line goes before it
        public bool Nested; // inside another such ingredient: a short line
        public string Note; // extra tooltip line, e.g. "Made at: Cauldron"
    }

    internal sealed class Food
    {
        public ItemDrop Item;
        public string Name;
        public Sprite Icon;
        public float Health, Stamina, Eitr, Duration, Healing;
        public float Total => Health + Stamina + Eitr;
        public FoodType Type;
        public string Station = "Gathered"; // where it is made, with level: "Cauldron 3"
        public string StationGroup = "Gathered"; // the same without level, for grouping
        public int Yield = 1;
        public readonly List<Ingredient> Ingredients = new List<Ingredient>();
        public bool Known;
        public string Effect; // a mead: what it does, on one line (null for food)
        public string EffectTip; // the same in full, for its tooltip
        public bool IsMead => Effect != null;
    }

    internal static class FoodData
    {
        private const int MaxDepth = 6;

        private static string L(string token) => Localization.instance != null ? Localization.instance.Localize(token) : token;

        private sealed class Part
        {
            public ItemDrop Item;
            public int Amount;
        }

        // How an ingredient is made: a crafting recipe, or cooked from one item on a cooking station.
        private sealed class Made
        {
            public string Note;
            public readonly List<Part> Parts = new List<Part>();
        }

        private static Dictionary<string, Recipe> _recipes;
        private static Dictionary<string, ItemDrop> _cooked;
        private static Dictionary<string, List<string>> _cookedOn;
        private static Dictionary<string, string> _how; // of those: made another way than cooking, and how to say so
        private const string Fermented = "Fermented in";

        private static string StationName(Recipe recipe)
        {
            if (recipe.m_craftingStation == null) return "Inventory";
            var name = L(recipe.m_craftingStation.m_name);
            return recipe.m_minStationLevel > 1 ? $"{name} {recipe.m_minStationLevel}" : name;
        }

        private static Made MadeFrom(ItemDrop item)
        {
            if (item == null) return null;
            if (_recipes.TryGetValue(item.m_itemData.m_shared.m_name, out var recipe))
            {
                if (recipe.m_requireOnlyOneIngredient) return null; // any one of several (raw fish from any fish): see Choices
                var made = new Made { Note = $"Made at: {StationName(recipe)}" + (recipe.m_amount > 1 ? $" (makes {recipe.m_amount})" : "") };
                foreach (var req in recipe.m_resources)
                    if (req?.m_resItem != null && req.m_amount > 0) made.Parts.Add(new Part { Item = req.m_resItem, Amount = req.m_amount });
                return made.Parts.Count > 0 ? made : null;
            }
            if (_cooked.TryGetValue(item.gameObject.name, out var from))
            {
                var made = new Made { Note = $"{(_how.TryGetValue(item.gameObject.name, out var how) ? how : "Cooked on")}: {string.Join(" / ", _cookedOn[item.gameObject.name])}" };
                made.Parts.Add(new Part { Item = from, Amount = 1 });
                return made;
            }
            return null;
        }

        private static Ingredient Plain(Part part)
        {
            var s = part.Item.m_itemData.m_shared;
            return new Ingredient { Name = L(s.m_name), Icon = s.m_icons != null && s.m_icons.Length > 0 ? s.m_icons[0] : null, Amount = part.Amount, Item = part.Item, Note = Choices(part.Item) };
        }

        // An item made from any one of several (raw fish from any fish) is not expanded: the tooltip names the choices.
        private static string Choices(ItemDrop item)
        {
            if (!_recipes.TryGetValue(item.m_itemData.m_shared.m_name, out var recipe) || !recipe.m_requireOnlyOneIngredient) return null;
            var names = new List<string>();
            foreach (var req in recipe.m_resources)
                if (req?.m_resItem != null && req.m_amount > 0) names.Add(L(req.m_resItem.m_itemData.m_shared.m_name));
            return names.Count > 0 ? $"Made at: {StationName(recipe)}, from any one of: {string.Join(", ", names)}" : null;
        }

        // Plain parts first, then each part that is made from others, followed by what it is made from (recursively).
        // "path" holds the items above this step, so a recipe loop (A from B from A) stops.
        private static void AddParts(List<Ingredient> into, List<Part> parts, HashSet<string> path, int depth, bool nested = false)
        {
            var made = new List<KeyValuePair<Part, Made>>();
            foreach (var part in parts)
            {
                var how = depth < MaxDepth && !path.Contains(part.Item.gameObject.name) ? MadeFrom(part.Item) : null;
                if (how == null) into.Add(Plain(part));
                else made.Add(new KeyValuePair<Part, Made>(part, how));
            }
            for (var i = 0; i < made.Count; i++)
            {
                var ingredient = Plain(made[i].Key);
                ingredient.Before = true;
                ingredient.Apart = parts.Count > 1;
                ingredient.Nested = nested;
                ingredient.Note = made[i].Value.Note;
                into.Add(ingredient);
                var name = made[i].Key.Item.gameObject.name;
                path.Add(name);
                AddParts(into, made[i].Value.Parts, path, depth + 1, nested || ingredient.Apart);
                path.Remove(name);
            }
        }

        internal static List<Food> Collect(Player player)
        {
            var result = new List<Food>();
            if (ObjectDB.instance == null) return result;

            // Item name -> first enabled crafting recipe that makes it.
            var recipes = _recipes = new Dictionary<string, Recipe>();
            foreach (var recipe in ObjectDB.instance.m_recipes)
            {
                if (recipe == null || !recipe.m_enabled || recipe.m_item == null) continue;
                var name = recipe.m_item.m_itemData.m_shared.m_name;
                if (!recipes.ContainsKey(name)) recipes[name] = recipe;
            }

            // Item prefab name -> what it is cooked from and on which stations.
            var cooked = _cooked = new Dictionary<string, ItemDrop>();
            var cookedOn = _cookedOn = new Dictionary<string, List<string>>();
            _how = new Dictionary<string, string>();
            var batches = new Dictionary<string, int>(); // what fermenters make, and how many at a time
            void Convert(ItemDrop from, ItemDrop to, string stationName)
            {
                var key = to.gameObject.name;
                if (!cooked.ContainsKey(key)) cooked[key] = from;
                if (!cookedOn.TryGetValue(key, out var list)) cookedOn[key] = list = new List<string>();
                if (!list.Contains(stationName)) list.Add(stationName);
            }
            string Title(GameObject prefab, string fallback)
            {
                var piece = prefab.GetComponent<Piece>();
                return L(piece != null && !string.IsNullOrEmpty(piece.m_name) ? piece.m_name : fallback);
            }
            if (ZNetScene.instance != null)
            {
                foreach (var prefab in ZNetScene.instance.m_prefabs)
                {
                    var station = prefab != null ? prefab.GetComponent<CookingStation>() : null;
                    if (station == null || station.m_conversion == null) continue;
                    foreach (var conversion in station.m_conversion)
                        if (conversion?.m_from != null && conversion.m_to != null) Convert(conversion.m_from, conversion.m_to, Title(prefab, station.m_name));
                }
                // Afterwards, so what can also be cooked stays cooked.
                foreach (var prefab in ZNetScene.instance.m_prefabs)
                {
                    var mill = prefab != null ? prefab.GetComponent<Smelter>() : null;
                    if (mill == null || mill.m_conversion == null) continue;
                    foreach (var conversion in mill.m_conversion)
                    {
                        if (conversion?.m_from == null || conversion.m_to == null) continue;
                        var key = conversion.m_to.gameObject.name;
                        if (!cooked.ContainsKey(key)) _how[key] = "Made in";
                        if (_how.ContainsKey(key)) Convert(conversion.m_from, conversion.m_to, Title(prefab, mill.m_name));
                    }
                }
                foreach (var prefab in ZNetScene.instance.m_prefabs)
                {
                    var fermenter = prefab != null ? prefab.GetComponent<Fermenter>() : null;
                    if (fermenter == null || fermenter.m_conversion == null) continue;
                    foreach (var conversion in fermenter.m_conversion)
                    {
                        if (conversion?.m_from == null || conversion.m_to == null) continue;
                        var key = conversion.m_to.gameObject.name;
                        if (!cooked.ContainsKey(key)) _how[key] = Fermented;
                        if (!_how.TryGetValue(key, out var how) || how != Fermented) continue;
                        Convert(conversion.m_from, conversion.m_to, Title(prefab, fermenter.m_name));
                        batches[key] = conversion.m_producedItems;
                    }
                }
            }

            var seen = new HashSet<string>();
            foreach (var prefab in ObjectDB.instance.m_items)
            {
                var item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                var shared = item?.m_itemData?.m_shared;
                if (shared == null || shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) continue;
                var mead = batches.TryGetValue(prefab.name, out var batch);
                if (!mead && shared.m_food <= 0f && shared.m_foodStamina <= 0f && shared.m_foodEitr <= 0f) continue;
                if (shared.m_icons == null || shared.m_icons.Length == 0 || !seen.Add(shared.m_name)) continue;

                var food = new Food
                {
                    Item = item,
                    Name = L(shared.m_name),
                    Icon = shared.m_icons[0],
                    Health = shared.m_food,
                    Stamina = shared.m_foodStamina,
                    Eitr = shared.m_foodEitr,
                    Duration = shared.m_foodBurnTime,
                    Healing = shared.m_foodRegen,
                    Known = player == null || player.IsKnownMaterial(shared.m_name) || player.IsRecipeKnown(shared.m_name),
                };
                food.Type = TypeOf(food);

                if (recipes.TryGetValue(shared.m_name, out var made))
                {
                    var station = made.m_craftingStation;
                    food.StationGroup = station != null ? L(station.m_name) : "Inventory";
                    food.Station = station != null && made.m_minStationLevel > 1 ? $"{food.StationGroup} {made.m_minStationLevel}" : food.StationGroup;
                    food.Yield = Mathf.Max(1, made.m_amount);
                    var parts = new List<Part>();
                    foreach (var req in made.m_resources)
                        if (req?.m_resItem != null && req.m_amount > 0) parts.Add(new Part { Item = req.m_resItem, Amount = req.m_amount });
                    AddParts(food.Ingredients, parts, new HashSet<string> { prefab.name }, 0);
                }
                else if (cooked.TryGetValue(prefab.name, out var from))
                {
                    var list = cookedOn[prefab.name];
                    food.StationGroup = list[0];
                    food.Station = list.Count > 1 ? list[0] + " +" + (list.Count - 1) : list[0];
                    // Made beforehand (unbaked pie, bread dough, ...): where that is made goes next to the station.
                    if (recipes.TryGetValue(from.m_itemData.m_shared.m_name, out var prep))
                        food.Station = $"{food.Station} <size=75%><color=#9a948a>({StationName(prep)})</color></size>";
                    AddParts(food.Ingredients, new List<Part> { new Part { Item = from, Amount = 1 } }, new HashSet<string> { prefab.name }, 0);
                    if (food.Ingredients.Count > 0 && food.Ingredients[0].Before)
                        food.Ingredients[0].Note += $"\nThen: {string.Join(" / ", list)}";
                }
                if (mead)
                {
                    food.Effect = EffectText(shared.m_consumeStatusEffect);
                    food.EffectTip = shared.m_consumeStatusEffect != null ? L(shared.m_consumeStatusEffect.GetTooltipString()).Trim() : "";
                    if (shared.m_consumeStatusEffect != null) food.Duration = shared.m_consumeStatusEffect.m_ttl;
                    food.Station = food.StationGroup; // where its base is brewed is in that ingredient's tooltip
                    food.Yield = Mathf.Max(1, batch);
                }
                result.Add(food);
            }
            return result;
        }

        // What a mead does, on one line: the numbers of its tooltip without the duration (that has its own column), or
        // its description when it has no numbers (Brew of Animal Whispers).
        private static string EffectText(StatusEffect effect)
        {
            if (effect == null) return "";
            var description = "";
            var numbers = new List<string>();
            foreach (var line in effect.GetTooltipString().Split('\n'))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.Contains("$se_ttl")) continue;
                if (text.Contains("<color")) numbers.Add(text);
                else if (description.Length == 0) description = text;
            }
            return L(numbers.Count > 0 ? string.Join(",  ", numbers) : description);
        }

        // Only "discovered" can change while playing; everything else is read once per session.
        internal static void UpdateKnown(List<Food> foods, Player player)
        {
            foreach (var food in foods)
            {
                var name = food.Item.m_itemData.m_shared.m_name;
                food.Known = player == null || player.IsKnownMaterial(name) || player.IsRecipeKnown(name);
            }
        }

        // What the food is mostly for, like the colors the game gives food: health (red), stamina (yellow), eitr (blue).
        private static FoodType TypeOf(Food f)
        {
            if (f.Eitr > 0f && f.Eitr >= Mathf.Max(f.Health, f.Stamina) * 0.8f) return FoodType.Eitr;
            if (f.Health >= f.Stamina * 1.4f) return FoodType.Health;
            if (f.Stamina >= f.Health * 1.4f) return FoodType.Stamina;
            return FoodType.Balanced;
        }

        internal static string TypeName(FoodType type)
        {
            switch (type)
            {
                case FoodType.Health: return "Health food";
                case FoodType.Stamina: return "Stamina food";
                case FoodType.Eitr: return "Eitr food";
                default: return "Balanced food";
            }
        }

        internal static Color TypeColor(FoodType type)
        {
            switch (type)
            {
                case FoodType.Health: return new Color(0.95f, 0.42f, 0.38f);
                case FoodType.Stamina: return new Color(1f, 0.8f, 0.3f);
                case FoodType.Eitr: return new Color(0.55f, 0.6f, 1f);
                default: return new Color(0.9f, 0.88f, 0.82f);
            }
        }
    }
}
