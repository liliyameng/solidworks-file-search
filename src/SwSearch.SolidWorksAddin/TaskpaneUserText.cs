using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using SwSearch.WebUi;

namespace SwSearch.SolidWorksAddin
{
    /// <summary>
    /// Language-selected UI strings for the SOLIDWORKS task pane (C# 5: this
    /// assembly is compiled by the legacy csc.exe toolchain). Simplified
    /// Chinese values are byte-identical to the historical literals.
    /// </summary>
    internal static class TaskpaneUserText
    {
        internal static string T(string language, string key)
        {
            Dictionary<string, string> table;
            if (Tables.TryGetValue(language, out table))
            {
                string value;
                if (table.TryGetValue(key, out value)) return value;
            }
            string fallback;
            return Chinese.TryGetValue(key, out fallback) ? fallback : key;
        }

        internal static string F(string language, string key, object arg0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                T(language, key),
                arg0);
        }

        /// <summary>
        /// Reads the persisted "language" preference from settings.json with
        /// the same data-root rules as every other SwSearch process. Any
        /// failure resolves to "auto" so the task pane keeps working.
        /// </summary>
        internal static string ReadConfiguredLanguage()
        {
            try
            {
                string isolated = Environment.GetEnvironmentVariable("SWSEARCH_DATA_ROOT");
                string directory = string.IsNullOrWhiteSpace(isolated)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SwSearch")
                    : Path.GetFullPath(isolated);
                string path = Path.Combine(directory, "settings.json");
                if (!File.Exists(path)) return UiLanguage.Auto;
                string text;
                using (StreamReader reader = new StreamReader(
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                {
                    text = reader.ReadToEnd();
                }
                if (string.IsNullOrEmpty(text)) return UiLanguage.Auto;
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.DeserializeObject(text)
                    as Dictionary<string, object>;
                if (root == null) return UiLanguage.Auto;
                object configured;
                string value;
                if (root.TryGetValue("language", out configured) &&
                    (value = configured as string) != null)
                {
                    return UiLanguage.SanitizeConfigured(value);
                }
                return UiLanguage.Auto;
            }
            catch (Exception exception)
            {
                AddinDiagnostics.WriteFailure("taskpane-language-read", exception);
                return UiLanguage.Auto;
            }
        }

        private static readonly Dictionary<string, string> Chinese = new Dictionary<string, string>
        {
            { "status.connecting", "正在连接搜索服务…" },
            { "status.loadingUi", "正在加载搜索界面…" },
            { "status.fileViewUnavailable", "文件结果视图暂不可用" },
            { "status.reconnectingBg", "正在重新连接后台更新…" },
            { "status.retryAction", "操作未能完成，请重试。" },
            { "status.libraryNotConfigured", "未配置 SOLIDWORKS 设计库路径" },
            { "status.librarySearching", "文件库 · 正在搜索…" },
            { "prefix.versionMismatch", "搜索服务版本不匹配：" },
            { "status.bgUnavailable", "后台更新暂不可用" },
            { "status.outsideLibraryRejected", "搜索服务返回了设计库外结果，已拒绝显示" },
            { "status.appExited", "桌面搜索已从托盘退出。请启动 SOLIDWORKS 文件检索后再试。" },
            { "result.libraryNotConfigured", "文件库 · 未配置 SOLIDWORKS 设计库路径" },
            { "result.libraryFileCount", "文件库 · 共 {0} 个文件" },
            { "scope.allLibraries", "全部设计库" },
            { "result.countSuffix", "{0} 个结果" },
            { "lib.noLibrary", "未配置 SOLIDWORKS 设计库路径。" },
            { "lib.outsideLibrary", "该文件不在 SOLIDWORKS 设计库路径内。" },
            { "lib.invalidPath", "目标文件路径无效。" },
            { "msg.emptyOrLarge", "消息为空或过大。" },
            { "msg.invalidJson", "消息不是有效 JSON。" },
            { "msg.envelopeUnsupported", "消息信封字段不受支持。" },
            { "msg.typeUnsupported", "消息类型不受支持。" },
            { "msg.requestIdInvalid", "requestId 无效。" },
            { "msg.payloadObject", "payload 必须是对象。" },
            { "msg.payloadNotAllowed", "该消息不接受 payload 字段。" },
            { "msg.readyViewInvalid", "ready 视图无效。" },
            { "msg.typeUnauthorized", "消息类型未授权。" },
            { "msg.searchFields", "搜索参数包含未授权字段。" },
            { "msg.searchInvalid", "搜索参数无效。" },
            { "msg.retryInvalid", "重试参数无效。" },
            { "msg.actionInvalid", "操作参数无效。" }
        };

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            { "status.connecting", "Connecting to the search service…" },
            { "status.loadingUi", "Loading the search interface…" },
            { "status.fileViewUnavailable", "The file results view is unavailable" },
            { "status.reconnectingBg", "Reconnecting background updates…" },
            { "status.retryAction", "The operation did not complete; please retry." },
            { "status.libraryNotConfigured", "No SOLIDWORKS design library path configured" },
            { "status.librarySearching", "File library · Searching…" },
            { "prefix.versionMismatch", "Search service version mismatch: " },
            { "status.bgUnavailable", "Background updates are unavailable" },
            { "status.outsideLibraryRejected", "The search service returned results outside the design library; they were refused" },
            { "status.appExited", "The desktop search app was closed from the tray. Start SOLIDWORKS File Search and retry." },
            { "result.libraryNotConfigured", "File library · No SOLIDWORKS design library path configured" },
            { "result.libraryFileCount", "File library · {0} files total" },
            { "scope.allLibraries", "All design libraries" },
            { "result.countSuffix", "{0} results" },
            { "lib.noLibrary", "No SOLIDWORKS design library path is configured." },
            { "lib.outsideLibrary", "The file is not inside the SOLIDWORKS design library path." },
            { "lib.invalidPath", "The target file path is invalid." },
            { "msg.emptyOrLarge", "The message is empty or too large." },
            { "msg.invalidJson", "The message is not valid JSON." },
            { "msg.envelopeUnsupported", "A message envelope field is not supported." },
            { "msg.typeUnsupported", "The message type is not supported." },
            { "msg.requestIdInvalid", "The requestId is invalid." },
            { "msg.payloadObject", "payload must be an object." },
            { "msg.payloadNotAllowed", "This message does not accept a payload." },
            { "msg.readyViewInvalid", "The ready view is invalid." },
            { "msg.typeUnauthorized", "The message type is not authorized." },
            { "msg.searchFields", "The search parameters contain unauthorized fields." },
            { "msg.searchInvalid", "The search parameters are invalid." },
            { "msg.retryInvalid", "The retry parameters are invalid." },
            { "msg.actionInvalid", "The action parameters are invalid." }
        };

        private static readonly Dictionary<string, string> Japanese = new Dictionary<string, string>
        {
            { "status.connecting", "検索サービスに接続中…" },
            { "status.loadingUi", "検索画面を読み込み中…" },
            { "status.fileViewUnavailable", "ファイル結果ビューは利用できません" },
            { "status.reconnectingBg", "バックグラウンド更新に再接続中…" },
            { "status.retryAction", "操作を完了できませんでした。再試行してください。" },
            { "status.libraryNotConfigured", "SOLIDWORKS デザイン ライブラリ パスが未設定です" },
            { "status.librarySearching", "ファイルライブラリ · 検索中…" },
            { "prefix.versionMismatch", "検索サービスのバージョンが一致しません：" },
            { "status.bgUnavailable", "バックグラウンド更新は利用できません" },
            { "status.outsideLibraryRejected", "デザイン ライブラリ外の結果が返されたため表示を拒否しました" },
            { "status.appExited", "デスクトップ検索アプリがトレイから終了しました。SOLIDWORKS ファイル検索を起動してから再試行してください。" },
            { "result.libraryNotConfigured", "ファイルライブラリ · デザイン ライブラリ パスが未設定です" },
            { "result.libraryFileCount", "ファイルライブラリ · 合計 {0} ファイル" },
            { "scope.allLibraries", "すべてのデザイン ライブラリ" },
            { "result.countSuffix", "{0} 件の結果" },
            { "lib.noLibrary", "SOLIDWORKS デザイン ライブラリ パスが未設定です。" },
            { "lib.outsideLibrary", "このファイルは SOLIDWORKS デザイン ライブラリ パス内にありません。" },
            { "lib.invalidPath", "対象ファイルのパスが無効です。" },
            { "msg.emptyOrLarge", "メッセージが空か大きすぎます。" },
            { "msg.invalidJson", "メッセージが有効な JSON ではありません。" },
            { "msg.envelopeUnsupported", "サポートされていないメッセージ エンベロープ フィールドです。" },
            { "msg.typeUnsupported", "サポートされていないメッセージ タイプです。" },
            { "msg.requestIdInvalid", "requestId が無効です。" },
            { "msg.payloadObject", "payload はオブジェクトである必要があります。" },
            { "msg.payloadNotAllowed", "このメッセージは payload を受け付けません。" },
            { "msg.readyViewInvalid", "ready ビューが無効です。" },
            { "msg.typeUnauthorized", "このメッセージ タイプは許可されていません。" },
            { "msg.searchFields", "検索パラメーターに未許可のフィールドが含まれています。" },
            { "msg.searchInvalid", "検索パラメーターが無効です。" },
            { "msg.retryInvalid", "再試行パラメーターが無効です。" },
            { "msg.actionInvalid", "アクション パラメーターが無効です。" }
        };

        private static readonly Dictionary<string, string> German = new Dictionary<string, string>
        {
            { "status.connecting", "Verbindung zum Suchdienst wird hergestellt…" },
            { "status.loadingUi", "Suchoberfläche wird geladen…" },
            { "status.fileViewUnavailable", "Die Datei-Ergebnisansicht ist nicht verfügbar" },
            { "status.reconnectingBg", "Hintergrundaktualisierung wird neu verbunden…" },
            { "status.retryAction", "Der Vorgang wurde nicht abgeschlossen; bitte erneut versuchen." },
            { "status.libraryNotConfigured", "Kein SOLIDWORKS-Designbibliothekspfad konfiguriert" },
            { "status.librarySearching", "Dateibibliothek · Suche läuft…" },
            { "prefix.versionMismatch", "Version des Suchdienstes stimmt nicht überein: " },
            { "status.bgUnavailable", "Hintergrundaktualisierungen sind nicht verfügbar" },
            { "status.outsideLibraryRejected", "Der Suchdienst lieferte Ergebnisse außerhalb der Designbibliothek; sie wurden abgelehnt" },
            { "status.appExited", "Die Desktop-Suche wurde über den Tray beendet. Bitte SOLIDWORKS Dateisuche starten und erneut versuchen." },
            { "result.libraryNotConfigured", "Dateibibliothek · Kein SOLIDWORKS-Designbibliothekspfad konfiguriert" },
            { "result.libraryFileCount", "Dateibibliothek · {0} Dateien insgesamt" },
            { "scope.allLibraries", "Alle Designbibliotheken" },
            { "result.countSuffix", "{0} Ergebnisse" },
            { "lib.noLibrary", "Es ist kein SOLIDWORKS-Designbibliothekspfad konfiguriert." },
            { "lib.outsideLibrary", "Die Datei liegt nicht im SOLIDWORKS-Designbibliothekspfad." },
            { "lib.invalidPath", "Der Zieldateipfad ist ungültig." },
            { "msg.emptyOrLarge", "Die Nachricht ist leer oder zu groß." },
            { "msg.invalidJson", "Die Nachricht ist kein gültiges JSON." },
            { "msg.envelopeUnsupported", "Ein Nachrichten-Umschlagfeld wird nicht unterstützt." },
            { "msg.typeUnsupported", "Der Nachrichtentyp wird nicht unterstützt." },
            { "msg.requestIdInvalid", "Die requestId ist ungültig." },
            { "msg.payloadObject", "payload muss ein Objekt sein." },
            { "msg.payloadNotAllowed", "Diese Nachricht akzeptiert keinen payload." },
            { "msg.readyViewInvalid", "Die ready-Ansicht ist ungültig." },
            { "msg.typeUnauthorized", "Der Nachrichtentyp ist nicht autorisiert." },
            { "msg.searchFields", "Die Suchparameter enthalten nicht autorisierte Felder." },
            { "msg.searchInvalid", "Die Suchparameter sind ungültig." },
            { "msg.retryInvalid", "Die Wiederholungsparameter sind ungültig." },
            { "msg.actionInvalid", "Die Aktionsparameter sind ungültig." }
        };

        private static readonly Dictionary<string, string> French = new Dictionary<string, string>
        {
            { "status.connecting", "Connexion au service de recherche…" },
            { "status.loadingUi", "Chargement de l'interface de recherche…" },
            { "status.fileViewUnavailable", "La vue des résultats de fichiers est indisponible" },
            { "status.reconnectingBg", "Reconnexion des mises à jour en arrière-plan…" },
            { "status.retryAction", "L'opération ne s'est pas terminée ; réessayez." },
            { "status.libraryNotConfigured", "Aucun chemin de bibliothèque de conception SOLIDWORKS configuré" },
            { "status.librarySearching", "Bibliothèque de fichiers · Recherche…" },
            { "prefix.versionMismatch", "Version du service de recherche incompatible : " },
            { "status.bgUnavailable", "Les mises à jour en arrière-plan sont indisponibles" },
            { "status.outsideLibraryRejected", "Le service de recherche a renvoyé des résultats hors de la bibliothèque de conception ; ils ont été refusés" },
            { "status.appExited", "L'application de recherche a été fermée depuis la barre d'état. Démarrez Recherche de fichiers SOLIDWORKS puis réessayez." },
            { "result.libraryNotConfigured", "Bibliothèque de fichiers · Aucun chemin de bibliothèque de conception SOLIDWORKS configuré" },
            { "result.libraryFileCount", "Bibliothèque de fichiers · {0} fichiers au total" },
            { "scope.allLibraries", "Toutes les bibliothèques de conception" },
            { "result.countSuffix", "{0} résultats" },
            { "lib.noLibrary", "Aucun chemin de bibliothèque de conception SOLIDWORKS n'est configuré." },
            { "lib.outsideLibrary", "Le fichier ne se trouve pas dans le chemin de bibliothèque de conception SOLIDWORKS." },
            { "lib.invalidPath", "Le chemin du fichier cible est invalide." },
            { "msg.emptyOrLarge", "Le message est vide ou trop volumineux." },
            { "msg.invalidJson", "Le message n'est pas un JSON valide." },
            { "msg.envelopeUnsupported", "Un champ d'enveloppe de message n'est pas pris en charge." },
            { "msg.typeUnsupported", "Le type de message n'est pas pris en charge." },
            { "msg.requestIdInvalid", "Le requestId est invalide." },
            { "msg.payloadObject", "payload doit être un objet." },
            { "msg.payloadNotAllowed", "Ce message n'accepte pas de payload." },
            { "msg.readyViewInvalid", "La vue ready est invalide." },
            { "msg.typeUnauthorized", "Le type de message n'est pas autorisé." },
            { "msg.searchFields", "Les paramètres de recherche contiennent des champs non autorisés." },
            { "msg.searchInvalid", "Les paramètres de recherche sont invalides." },
            { "msg.retryInvalid", "Les paramètres de réessai sont invalides." },
            { "msg.actionInvalid", "Les paramètres d'action sont invalides." }
        };

        private static readonly Dictionary<string, string> Spanish = new Dictionary<string, string>
        {
            { "status.connecting", "Conectando con el servicio de búsqueda…" },
            { "status.loadingUi", "Cargando la interfaz de búsqueda…" },
            { "status.fileViewUnavailable", "La vista de resultados de archivos no está disponible" },
            { "status.reconnectingBg", "Reconectando las actualizaciones en segundo plano…" },
            { "status.retryAction", "La operación no se completó; reintente." },
            { "status.libraryNotConfigured", "Sin ruta de biblioteca de diseño SOLIDWORKS configurada" },
            { "status.librarySearching", "Biblioteca de archivos · Buscando…" },
            { "prefix.versionMismatch", "Versión del servicio de búsqueda no compatible: " },
            { "status.bgUnavailable", "Las actualizaciones en segundo plano no están disponibles" },
            { "status.outsideLibraryRejected", "El servicio de búsqueda devolvió resultados fuera de la biblioteca de diseño; se rechazaron" },
            { "status.appExited", "La aplicación de búsqueda se cerró desde la bandeja. Inicie Búsqueda de archivos SOLIDWORKS y reintente." },
            { "result.libraryNotConfigured", "Biblioteca de archivos · Sin ruta de biblioteca de diseño SOLIDWORKS configurada" },
            { "result.libraryFileCount", "Biblioteca de archivos · {0} archivos en total" },
            { "scope.allLibraries", "Todas las bibliotecas de diseño" },
            { "result.countSuffix", "{0} resultados" },
            { "lib.noLibrary", "No hay ninguna ruta de biblioteca de diseño SOLIDWORKS configurada." },
            { "lib.outsideLibrary", "El archivo no está dentro de la ruta de la biblioteca de diseño SOLIDWORKS." },
            { "lib.invalidPath", "La ruta del archivo de destino no es válida." },
            { "msg.emptyOrLarge", "El mensaje está vacío o es demasiado grande." },
            { "msg.invalidJson", "El mensaje no es un JSON válido." },
            { "msg.envelopeUnsupported", "Un campo del sobre del mensaje no está admitido." },
            { "msg.typeUnsupported", "El tipo de mensaje no está admitido." },
            { "msg.requestIdInvalid", "El requestId no es válido." },
            { "msg.payloadObject", "payload debe ser un objeto." },
            { "msg.payloadNotAllowed", "Este mensaje no acepta payload." },
            { "msg.readyViewInvalid", "La vista ready no es válida." },
            { "msg.typeUnauthorized", "El tipo de mensaje no está autorizado." },
            { "msg.searchFields", "Los parámetros de búsqueda contienen campos no autorizados." },
            { "msg.searchInvalid", "Los parámetros de búsqueda no son válidos." },
            { "msg.retryInvalid", "Los parámetros de reintento no son válidos." },
            { "msg.actionInvalid", "Los parámetros de acción no son válidos." }
        };

        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        static TaskpaneUserText()
        {
            Tables[UiLanguage.Chinese] = Chinese;
            Tables["en"] = English;
            Tables["ja"] = Japanese;
            Tables["de"] = German;
            Tables["fr"] = French;
            Tables["es"] = Spanish;
        }
    }
}
