// Auto Map Pins, its own texts in the game's language: the filter buttons' tips and the list of kinds. Names of things,
// places and biomes come from the game itself (in every language it has); these few texts are translated here for the
// most played languages, English for the rest. Each language is one row in the order of Keys.
using System.Collections.Generic;

namespace AutoMapPins
{
    internal static class Lang
    {
        private static readonly string[] Keys =
        {
            "Portals", "Bosses and traders", "Dungeons and caves", "Other places", "Ore and deposits", "Plants and pickups", "Spawners",
            "Chests", "Epic Loot bounties and treasure maps", "hidden", "shown", "Click to show these pins", "Click to hide these pins",
            "Every kind of pin", "Click for the list: switch each kind on or off", "Kinds of pins",
            "Each kind on or off, in all biomes at once (as in F1).",
            "Off: its pins hidden, none added. Partly: on in some biomes. Esc closes.",
            "Search a kind...", "Nothing found", "On", "Off", "Partly",
        };

        private static readonly Dictionary<string, string[]> Rows = new Dictionary<string, string[]>
        {
            ["German"] = new[]
            {
                "Portale", "Bosse und Händler", "Dungeons und Höhlen", "Andere Orte", "Erze und Vorkommen", "Pflanzen und Sammelbares", "Spawner",
                "Truhen", "Epic-Loot-Kopfgelder und Schatzkarten", "ausgeblendet", "sichtbar", "Klicken, um diese Pins anzuzeigen",
                "Klicken, um diese Pins auszublenden",
                "Alle Arten von Pins", "Klicken für die Liste: jede Art ein- oder ausschalten", "Arten von Pins",
                "Jede Art an oder aus, in allen Biomen zugleich (wie in F1).",
                "Aus: Pins ausgeblendet, keine neuen. Teils: in manchen Biomen an. Esc schließt.",
                "Art suchen...", "Nichts gefunden", "An", "Aus", "Teils",
            },
            ["French"] = new[]
            {
                "Portails", "Boss et marchands", "Donjons et grottes", "Autres lieux", "Minerais et gisements", "Plantes et ramassables", "Générateurs",
                "Coffres", "Primes et cartes au trésor d'Epic Loot", "masqué", "affiché", "Cliquer pour afficher ces épingles",
                "Cliquer pour masquer ces épingles",
                "Tous les types d'épingles", "Cliquer pour la liste : activer ou désactiver chaque type", "Types d'épingles",
                "Chaque type activé ou non, dans tous les biomes (comme dans F1).",
                "Désactivé : masquées, pas de nouvelles. Partiel : certains biomes. Échap ferme.",
                "Chercher un type...", "Rien trouvé", "Activé", "Désactivé", "Partiel",
            },
            ["Spanish"] = new[]
            {
                "Portales", "Jefes y comerciantes", "Mazmorras y cuevas", "Otros lugares", "Minerales y yacimientos", "Plantas y recolectables", "Generadores",
                "Cofres", "Recompensas y mapas del tesoro de Epic Loot", "oculto", "visible", "Haz clic para mostrar estos marcadores",
                "Haz clic para ocultar estos marcadores",
                "Todos los tipos de marcadores", "Haz clic para la lista: activa o desactiva cada tipo", "Tipos de marcadores",
                "Cada tipo activado o no, en todos los biomas (como en F1).",
                "Desactivado: ocultos, sin nuevos. Parcial: solo algunos biomas. Esc cierra.",
                "Buscar un tipo...", "No se encontró nada", "Activado", "Desactivado", "Parcial",
            },
            ["Russian"] = new[]
            {
                "Порталы", "Боссы и торговцы", "Подземелья и пещеры", "Другие места", "Руды и залежи", "Растения и находки", "Спаунеры",
                "Сундуки", "Награды и карты сокровищ Epic Loot", "скрыто", "показано", "Нажмите, чтобы показать эти метки",
                "Нажмите, чтобы скрыть эти метки",
                "Все виды меток", "Нажмите для списка: включите или выключите каждый вид", "Виды меток",
                "Каждый вид вкл. или выкл., во всех биомах сразу (как в F1).",
                "Выкл.: метки скрыты, новых нет. Частично: в некоторых биомах. Esc закрывает.",
                "Найти вид...", "Ничего не найдено", "Вкл.", "Выкл.", "Частично",
            },
            ["Polish"] = new[]
            {
                "Portale", "Bossowie i handlarze", "Lochy i jaskinie", "Inne miejsca", "Rudy i złoża", "Rośliny i znaleziska", "Spawnery",
                "Skrzynie", "Nagrody i mapy skarbów Epic Loot", "ukryte", "widoczne", "Kliknij, aby pokazać te znaczniki",
                "Kliknij, aby ukryć te znaczniki",
                "Wszystkie rodzaje znaczników", "Kliknij, aby otworzyć listę: włącz lub wyłącz każdy rodzaj", "Rodzaje znaczników",
                "Każdy rodzaj wł. lub wył., we wszystkich biomach (jak w F1).",
                "Wył.: ukryte, bez nowych. Częściowo: w niektórych biomach. Esc zamyka.",
                "Szukaj rodzaju...", "Nic nie znaleziono", "Wł.", "Wył.", "Częściowo",
            },
            ["Portuguese_Brazilian"] = new[]
            {
                "Portais", "Chefes e comerciantes", "Masmorras e cavernas", "Outros lugares", "Minérios e depósitos", "Plantas e coletáveis", "Geradores",
                "Baús", "Recompensas e mapas do tesouro do Epic Loot", "oculto", "visível", "Clique para mostrar estes marcadores",
                "Clique para ocultar estes marcadores",
                "Todos os tipos de marcadores", "Clique para a lista: ligue ou desligue cada tipo", "Tipos de marcadores",
                "Cada tipo ligado ou não, em todos os biomas (como no F1).",
                "Desligado: ocultos, sem novos. Parcial: só alguns biomas. Esc fecha.",
                "Buscar um tipo...", "Nada encontrado", "Ligado", "Desligado", "Parcial",
            },
            ["Chinese"] = new[]
            {
                "传送门", "首领和商人", "地下城和洞穴", "其他地点", "矿石和矿床", "植物和可拾取物", "刷怪点",
                "宝箱", "Epic Loot 悬赏和藏宝图", "已隐藏", "已显示", "点击显示这些标记",
                "点击隐藏这些标记",
                "所有标记类型", "点击打开列表：逐个开启或关闭每种类型", "标记类型",
                "每种类型开启或关闭，作用于所有生物群系（同 F1）。",
                "关闭：标记隐藏且不再新增。部分：仅部分生物群系开启。Esc 关闭。",
                "搜索类型...", "未找到", "开启", "关闭", "部分",
            },
            ["Italian"] = new[]
            {
                "Portali", "Boss e mercanti", "Dungeon e caverne", "Altri luoghi", "Minerali e giacimenti", "Piante e raccoglibili", "Generatori",
                "Forzieri", "Taglie e mappe del tesoro di Epic Loot", "nascosto", "visibile", "Clicca per mostrare questi segnalini",
                "Clicca per nascondere questi segnalini",
                "Tutti i tipi di segnalini", "Clicca per la lista: attiva o disattiva ogni tipo", "Tipi di segnalini",
                "Ogni tipo attivo o no, in tutti i biomi (come in F1).",
                "Disattivato: nascosti, nessun nuovo. Parziale: solo alcuni biomi. Esc chiude.",
                "Cerca un tipo...", "Nessun risultato", "Attivo", "Disattivato", "Parziale",
            },
        };

        private static string _language;
        private static Dictionary<string, string> _texts;

        // The text in the game's language, or as given (English) when there is no translation.
        internal static string T(string english)
        {
            var language = Localization.instance != null ? Localization.instance.GetSelectedLanguage() : "English";
            if (language != _language)
            {
                _language = language;
                _texts = null;
                if (language != null && Rows.TryGetValue(language, out var row) && row.Length == Keys.Length)
                {
                    _texts = new Dictionary<string, string>();
                    for (var i = 0; i < Keys.Length; i++) _texts[Keys[i]] = row[i];
                }
            }
            return _texts != null && _texts.TryGetValue(english, out var text) ? text : english;
        }

        // Every row has a text for every key (checked once at startup; a broken row falls back to English).
        internal static string Check()
        {
            foreach (var pair in Rows)
                if (pair.Value.Length != Keys.Length) return $"{pair.Key}: {pair.Value.Length} texts, {Keys.Length} wanted";
            return null;
        }
    }
}
