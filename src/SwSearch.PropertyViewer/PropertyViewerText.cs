using System.Collections.Generic;
using System.IO;
using SwSearch.WebUi;

namespace SwSearch.PropertyViewer;

/// <summary>
/// Language-selected UI strings for the native property viewer window.
/// The effective language is resolved once per viewer launch from the shared
/// settings.json "language" preference (auto follows the OS UI language).
/// Simplified Chinese values are byte-identical to the historical literals.
/// </summary>
internal static class PropertyViewerText
{
    internal static string ResolveConfiguredLanguage()
    {
        try
        {
            var isolated = Environment.GetEnvironmentVariable("SWSEARCH_DATA_ROOT");
            var directory = string.IsNullOrWhiteSpace(isolated)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SwSearch")
                : Path.GetFullPath(isolated);
            var path = Path.Combine(directory, "settings.json");
            if (!File.Exists(path))
            {
                return UiLanguage.Auto;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("language", out var node) ||
                node.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return UiLanguage.Auto;
            }
            return UiLanguage.SanitizeConfigured(node.GetString() ?? string.Empty);
        }
        catch
        {
            return UiLanguage.Auto;
        }
    }

    internal static string Get(string language, string key)
    {
        if (Tables.TryGetValue(language, out var table) && table.TryGetValue(key, out var value))
        {
            return value;
        }
        return Chinese.TryGetValue(key, out var fallback) ? fallback : key;
    }

    private static readonly IReadOnlyDictionary<string, string> Chinese = new Dictionary<string, string>
    {
        ["window.title"] = "查看 SOLIDWORKS 属性",
        ["label.show"] = "显示：",
        ["label.configuration"] = "配置：",
        ["column.name"] = "属性名称",
        ["column.value"] = "属性值",
        ["mode.custom"] = "自定义属性",
        ["mode.configuration"] = "配置属性",
        ["file.cannotOpen"] = "无法打开文件",
        ["file.invalidParameter"] = "文件参数无效。",
        ["status.reading"] = "暂无已提取属性，正在读取此文件",
        ["status.displayedFreshReading"] = "属性已显示 · 正在鲜读",
        ["status.fileMissing"] = "文件不存在，可能已被移动或删除。",
        ["status.cannotRead"] = "暂时无法读取此文件的属性。",
        ["status.updatedFresh"] = "属性已更新（鲜读）",
        ["status.readFresh"] = "属性已读取（鲜读）",
        ["status.displayedIncompletePrefix"] = "属性已显示 · 鲜读未完成，当前显示缓存 · ",
        ["status.displayedFailedPrefix"] = "属性已显示 · 鲜读失败，当前显示缓存 · ",
        ["status.displayedFailedShort"] = "属性已显示 · 鲜读失败，当前显示缓存",
        ["status.cannotFreshRead"] = "暂时无法鲜读此文件的属性。",
        ["row.statusKey"] = "状态",
        ["row.noProperties"] = "暂无已提取属性",
        ["err.fileMissing"] = "文件不存在。",
        ["err.brokerTimeout"] = "鲜读超时（6.5 秒），可稍后重试。",
        ["err.brokerUnavailable"] = "属性服务未运行或暂不可用。",
        ["err.brokerMismatch"] = "属性服务版本不匹配。",
    };

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["window.title"] = "View SOLIDWORKS Properties",
        ["label.show"] = "Show:",
        ["label.configuration"] = "Configuration:",
        ["column.name"] = "Property name",
        ["column.value"] = "Property value",
        ["mode.custom"] = "Custom properties",
        ["mode.configuration"] = "Configuration properties",
        ["file.cannotOpen"] = "Cannot open the file",
        ["file.invalidParameter"] = "The file parameter is invalid.",
        ["status.reading"] = "No extracted properties yet; this file is being read",
        ["status.displayedFreshReading"] = "Properties shown · fresh read in progress",
        ["status.fileMissing"] = "The file does not exist; it may have been moved or deleted.",
        ["status.cannotRead"] = "The properties of this file cannot be read right now.",
        ["status.updatedFresh"] = "Properties updated (fresh read)",
        ["status.readFresh"] = "Properties read (fresh read)",
        ["status.displayedIncompletePrefix"] = "Properties shown · fresh read incomplete, cache shown · ",
        ["status.displayedFailedPrefix"] = "Properties shown · fresh read failed, cache shown · ",
        ["status.displayedFailedShort"] = "Properties shown · fresh read failed, cache shown",
        ["status.cannotFreshRead"] = "A fresh read of this file's properties is unavailable right now.",
        ["row.statusKey"] = "Status",
        ["row.noProperties"] = "No extracted properties",
        ["err.fileMissing"] = "The file does not exist.",
        ["err.brokerTimeout"] = "Fresh read timed out (6.5 s); retry later.",
        ["err.brokerUnavailable"] = "The property service is not running or is unavailable.",
        ["err.brokerMismatch"] = "The property service version does not match.",
    };

    private static readonly IReadOnlyDictionary<string, string> Japanese = new Dictionary<string, string>
    {
        ["window.title"] = "SOLIDWORKS プロパティを表示",
        ["label.show"] = "表示：",
        ["label.configuration"] = "コンフィギュレーション：",
        ["column.name"] = "プロパティ名",
        ["column.value"] = "プロパティ値",
        ["mode.custom"] = "カスタム プロパティ",
        ["mode.configuration"] = "コンフィギュレーション プロパティ",
        ["file.cannotOpen"] = "ファイルを開けません",
        ["file.invalidParameter"] = "ファイル パラメーターが無効です。",
        ["status.reading"] = "抽出済みプロパティはまだありません。このファイルを読み取り中です",
        ["status.displayedFreshReading"] = "プロパティ表示中 · 鮮読（フレッシュ読み取り）を実行中",
        ["status.fileMissing"] = "ファイルが存在しません。移動または削除された可能性があります。",
        ["status.cannotRead"] = "このファイルのプロパティを一時的に読み取れません。",
        ["status.updatedFresh"] = "プロパティを更新しました（鮮読）",
        ["status.readFresh"] = "プロパティを読み取りました（鮮読）",
        ["status.displayedIncompletePrefix"] = "プロパティ表示中 · 鮮読未完了、キャッシュを表示中 · ",
        ["status.displayedFailedPrefix"] = "プロパティ表示中 · 鮮読失敗、キャッシュを表示中 · ",
        ["status.displayedFailedShort"] = "プロパティ表示中 · 鮮読失敗、キャッシュを表示中",
        ["status.cannotFreshRead"] = "このファイルのプロパティを鮮読できません。",
        ["row.statusKey"] = "状態",
        ["row.noProperties"] = "抽出済みプロパティはありません",
        ["err.fileMissing"] = "ファイルが存在しません。",
        ["err.brokerTimeout"] = "鮮読がタイムアウトしました（6.5 秒）。後でもう一度お試しください。",
        ["err.brokerUnavailable"] = "プロパティ サービスが実行されていないか利用できません。",
        ["err.brokerMismatch"] = "プロパティ サービスのバージョンが一致しません。",
    };

    private static readonly IReadOnlyDictionary<string, string> German = new Dictionary<string, string>
    {
        ["window.title"] = "SOLIDWORKS-Eigenschaften anzeigen",
        ["label.show"] = "Anzeigen:",
        ["label.configuration"] = "Konfiguration:",
        ["column.name"] = "Eigenschaftenname",
        ["column.value"] = "Eigenschaftenwert",
        ["mode.custom"] = "Benutzerdefinierte Eigenschaften",
        ["mode.configuration"] = "Konfigurationseigenschaften",
        ["file.cannotOpen"] = "Datei kann nicht geöffnet werden",
        ["file.invalidParameter"] = "Der Dateiparameter ist ungültig.",
        ["status.reading"] = "Noch keine extrahierten Eigenschaften; diese Datei wird gelesen",
        ["status.displayedFreshReading"] = "Eigenschaften angezeigt · Frischlesen läuft",
        ["status.fileMissing"] = "Die Datei ist nicht vorhanden; sie wurde möglicherweise verschoben oder gelöscht.",
        ["status.cannotRead"] = "Die Eigenschaften dieser Datei können derzeit nicht gelesen werden.",
        ["status.updatedFresh"] = "Eigenschaften aktualisiert (Frischlesen)",
        ["status.readFresh"] = "Eigenschaften gelesen (Frischlesen)",
        ["status.displayedIncompletePrefix"] = "Eigenschaften angezeigt · Frischlesen unvollständig, Cache angezeigt · ",
        ["status.displayedFailedPrefix"] = "Eigenschaften angezeigt · Frischlesen fehlgeschlagen, Cache angezeigt · ",
        ["status.displayedFailedShort"] = "Eigenschaften angezeigt · Frischlesen fehlgeschlagen, Cache angezeigt",
        ["status.cannotFreshRead"] = "Ein Frischlesen der Eigenschaften dieser Datei ist derzeit nicht möglich.",
        ["row.statusKey"] = "Status",
        ["row.noProperties"] = "Keine extrahierten Eigenschaften",
        ["err.fileMissing"] = "Die Datei ist nicht vorhanden.",
        ["err.brokerTimeout"] = "Zeitüberschreitung beim Frischlesen (6,5 s); später erneut versuchen.",
        ["err.brokerUnavailable"] = "Der Eigenschaftendienst läuft nicht oder ist nicht verfügbar.",
        ["err.brokerMismatch"] = "Die Version des Eigenschaftendienstes stimmt nicht überein.",
    };

    private static readonly IReadOnlyDictionary<string, string> French = new Dictionary<string, string>
    {
        ["window.title"] = "Voir les propriétés SOLIDWORKS",
        ["label.show"] = "Afficher :",
        ["label.configuration"] = "Configuration :",
        ["column.name"] = "Nom de propriété",
        ["column.value"] = "Valeur de propriété",
        ["mode.custom"] = "Propriétés personnalisées",
        ["mode.configuration"] = "Propriétés de configuration",
        ["file.cannotOpen"] = "Impossible d'ouvrir le fichier",
        ["file.invalidParameter"] = "Le paramètre de fichier est invalide.",
        ["status.reading"] = "Aucune propriété extraite pour le moment ; ce fichier est en cours de lecture",
        ["status.displayedFreshReading"] = "Propriétés affichées · lecture fraîche en cours",
        ["status.fileMissing"] = "Le fichier n'existe pas ; il a peut-être été déplacé ou supprimé.",
        ["status.cannotRead"] = "Les propriétés de ce fichier ne peuvent pas être lues pour le moment.",
        ["status.updatedFresh"] = "Propriétés mises à jour (lecture fraîche)",
        ["status.readFresh"] = "Propriétés lues (lecture fraîche)",
        ["status.displayedIncompletePrefix"] = "Propriétés affichées · lecture fraîche incomplète, cache affiché · ",
        ["status.displayedFailedPrefix"] = "Propriétés affichées · lecture fraîche échouée, cache affiché · ",
        ["status.displayedFailedShort"] = "Propriétés affichées · lecture fraîche échouée, cache affiché",
        ["status.cannotFreshRead"] = "Une lecture fraîche des propriétés de ce fichier est indisponible pour le moment.",
        ["row.statusKey"] = "État",
        ["row.noProperties"] = "Aucune propriété extraite",
        ["err.fileMissing"] = "Le fichier n'existe pas.",
        ["err.brokerTimeout"] = "Délai dépassé pour la lecture fraîche (6,5 s) ; réessayez plus tard.",
        ["err.brokerUnavailable"] = "Le service de propriétés ne fonctionne pas ou est indisponible.",
        ["err.brokerMismatch"] = "La version du service de propriétés ne correspond pas.",
    };

    private static readonly IReadOnlyDictionary<string, string> Spanish = new Dictionary<string, string>
    {
        ["window.title"] = "Ver propiedades de SOLIDWORKS",
        ["label.show"] = "Mostrar:",
        ["label.configuration"] = "Configuración:",
        ["column.name"] = "Nombre de propiedad",
        ["column.value"] = "Valor de propiedad",
        ["mode.custom"] = "Propiedades personalizadas",
        ["mode.configuration"] = "Propiedades de configuración",
        ["file.cannotOpen"] = "No se puede abrir el archivo",
        ["file.invalidParameter"] = "El parámetro de archivo no es válido.",
        ["status.reading"] = "Aún no hay propiedades extraídas; se está leyendo este archivo",
        ["status.displayedFreshReading"] = "Propiedades mostradas · lectura fresca en curso",
        ["status.fileMissing"] = "El archivo no existe; puede haberse movido o eliminado.",
        ["status.cannotRead"] = "No se pueden leer las propiedades de este archivo en este momento.",
        ["status.updatedFresh"] = "Propiedades actualizadas (lectura fresca)",
        ["status.readFresh"] = "Propiedades leídas (lectura fresca)",
        ["status.displayedIncompletePrefix"] = "Propiedades mostradas · lectura fresca incompleta, caché mostrada · ",
        ["status.displayedFailedPrefix"] = "Propiedades mostradas · lectura fresca fallida, caché mostrada · ",
        ["status.displayedFailedShort"] = "Propiedades mostradas · lectura fresca fallida, caché mostrada",
        ["status.cannotFreshRead"] = "La lectura fresca de las propiedades de este archivo no está disponible ahora.",
        ["row.statusKey"] = "Estado",
        ["row.noProperties"] = "No hay propiedades extraídas",
        ["err.fileMissing"] = "El archivo no existe.",
        ["err.brokerTimeout"] = "Tiempo de espera agotado en la lectura fresca (6,5 s); reintente más tarde.",
        ["err.brokerUnavailable"] = "El servicio de propiedades no se está ejecutando o no está disponible.",
        ["err.brokerMismatch"] = "La versión del servicio de propiedades no coincide.",
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(System.StringComparer.Ordinal)
        {
            [UiLanguage.Chinese] = Chinese,
            ["en"] = English,
            ["ja"] = Japanese,
            ["de"] = German,
            ["fr"] = French,
            ["es"] = Spanish,
        };
}
