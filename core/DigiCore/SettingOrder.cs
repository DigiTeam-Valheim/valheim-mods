// F1 (Configuration Manager) lists settings alphabetically unless told otherwise. This gives every setting the order
// in which the mod declares it, so the main switch comes first and related settings stay together. Settings that
// already have an order keep theirs.
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

internal static class SettingOrder
{
    // Read by Configuration Manager by name.
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }

    internal static void Apply(ConfigFile config)
    {
        var descriptionField = AccessTools.Field(typeof(ConfigEntryBase), "<Description>k__BackingField");
        if (descriptionField == null) return;
        var order = 1000;
        foreach (var key in config.Keys.ToList())
        {
            var entry = config[key];
            var description = entry.Description;
            var tags = description?.Tags ?? new object[0];
            var attributes = tags.FirstOrDefault(t => t != null && t.GetType().Name == "ConfigurationManagerAttributes");
            if (attributes != null)
            {
                var orderField = AccessTools.Field(attributes.GetType(), "Order");
                if (orderField != null && orderField.GetValue(attributes) == null) orderField.SetValue(attributes, order);
            }
            else
            {
                descriptionField.SetValue(entry, new ConfigDescription(description?.Description ?? "", description?.AcceptableValues,
                    tags.Concat(new object[] { new ConfigurationManagerAttributes { Order = order } }).ToArray()));
            }
            order--;
        }
    }
}
