using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace SwSearch.SolidWorksAddin
{
    internal sealed class TaskpaneWebCommand
    {
        internal string Type { get; private set; }
        internal string RequestId { get; private set; }
        internal string Text { get; private set; }
        internal int Mode { get; private set; }
        internal int[] Kinds { get; private set; }
        internal int Scope { get; private set; }
        internal string RootPath { get; private set; }
        internal bool Immediate { get; private set; }
        internal string Action { get; private set; }
        internal int ResultIndex { get; private set; }
        internal string ColumnId { get; private set; }
        internal double ColumnWidth { get; private set; }
        internal string ResultPath { get; private set; }

        internal TaskpaneWebCommand(
            string type,
            string requestId,
            string text,
            int mode,
            int[] kinds,
            int scope,
            string rootPath,
            bool immediate,
            string action,
            int resultIndex,
            string columnId,
            double columnWidth,
            string resultPath)
        {
            Type = type;
            RequestId = requestId;
            Text = text ?? String.Empty;
            Mode = mode;
            Kinds = kinds;
            Scope = scope;
            RootPath = rootPath ?? String.Empty;
            Immediate = immediate;
            Action = action;
            ResultIndex = resultIndex;
            ColumnId = columnId;
            ColumnWidth = columnWidth;
            ResultPath = resultPath;
        }
    }

    internal sealed class TaskpaneWebCommandEventArgs : EventArgs
    {
        internal TaskpaneWebCommandEventArgs(TaskpaneWebCommand command)
        {
            Command = command;
        }

        internal TaskpaneWebCommand Command { get; private set; }
    }

    internal sealed class TaskpaneWebHostFailureEventArgs : EventArgs
    {
        internal TaskpaneWebHostFailureEventArgs(string stage, Exception exception)
        {
            Stage = stage;
            Exception = exception;
        }

        internal string Stage { get; private set; }
        internal Exception Exception { get; private set; }
    }

    /// <summary>
    /// Parses the only messages the compact search toolbar may send. Result
    /// actions and Shell drag/drop originate exclusively from the native list.
    /// </summary>
    internal static class TaskpaneWebBridgePolicy
    {
        private static readonly HashSet<string> AllowedTypes = new HashSet<string>(
            new[]
            {
                "ready", "search", "retry"
            },
            StringComparer.Ordinal);

        // Shared by the two UI-thread call sites only. WebView2 raises
        // WebMessageReceived on the UI STA that created the controller, and the
        // test harness drives TryParse sequentially, so no two deserializations
        // run concurrently. CLR4 guarantees one-time static initialization.
        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = 65536 };

        private static readonly string[] EnvelopeAllowedKeys = { "type", "payload", "requestId" };
        private static readonly string[] ReadyPayloadAllowedKeys = { "view" };
        private static readonly string[] SearchPayloadAllowedKeys =
        {
            "query", "text", "kinds", "types", "immediate", "clientSequence", "inputDelayMs"
        };
        private static readonly string[] RetryPayloadAllowedKeys = { "target" };
        private static readonly string[] TypeFilterAllowedKeys = { "part", "assembly", "drawing" };

        internal static bool TryParse(string json, out TaskpaneWebCommand command, out string error)
        {
            command = null;
            error = null;
            if (String.IsNullOrWhiteSpace(json) || json.Length > 65536)
            {
                error = "消息为空或过大。";
                return false;
            }

            IDictionary<string, object> envelope;
            try
            {
                envelope = Serializer.DeserializeObject(json) as IDictionary<string, object>;
            }
            catch (Exception)
            {
                error = "消息不是有效 JSON。";
                return false;
            }

            if (envelope == null || HasUnexpectedKeys(envelope, EnvelopeAllowedKeys))
            {
                error = "消息信封字段不受支持。";
                return false;
            }

            string type = ReadString(envelope, "type", 32);
            if (type == null || !AllowedTypes.Contains(type))
            {
                error = "消息类型不受支持。";
                return false;
            }

            string requestId = null;
            if (envelope.ContainsKey("requestId"))
            {
                requestId = ReadString(envelope, "requestId", 80);
                if (requestId == null || !requestId.All(IsRequestIdCharacter))
                {
                    error = "requestId 无效。";
                    return false;
                }
            }

            IDictionary<string, object> payload = EmptyPayload(envelope);
            if (payload == null)
            {
                error = "payload 必须是对象。";
                return false;
            }

            if (type == "ready")
            {
                if (HasUnexpectedKeys(payload, ReadyPayloadAllowedKeys))
                {
                    error = "该消息不接受 payload 字段。";
                    return false;
                }
                if (payload.ContainsKey("view") &&
                    !String.Equals(ReadString(payload, "view", 16), "taskpane", StringComparison.Ordinal))
                {
                    error = "ready 视图无效。";
                    return false;
                }
                command = New(type, requestId);
                return true;
            }

            if (type == "search") return TryParseSearch(payload, requestId, out command, out error);
            if (type == "retry")
                return TryParseSimpleAction(type, payload, requestId, out command, out error);
            error = "消息类型未授权。";
            return false;
        }

        private static bool TryParseSearch(
            IDictionary<string, object> payload,
            string requestId,
            out TaskpaneWebCommand command,
            out string error)
        {
            command = null;
            error = null;
            // kinds/types are accepted only for an already-loaded older page;
            // they are validated below and intentionally never reach a query.
            if (HasUnexpectedKeys(payload, SearchPayloadAllowedKeys))
            {
                error = "搜索参数包含未授权字段。";
                return false;
            }

            string text = payload.ContainsKey("query")
                ? ReadString(payload, "query", 512)
                : (payload.ContainsKey("text") ? ReadString(payload, "text", 4096) : String.Empty);
            bool immediate;
            if (text == null || !ValidateLegacyKinds(payload) ||
                !TryReadBoolean(payload, "immediate", false, out immediate) ||
                !TryReadOptionalInt(payload, "clientSequence", 0, Int32.MaxValue) ||
                !TryReadOptionalInt(payload, "inputDelayMs", 0, 5000))
            {
                error = "搜索参数无效。";
                return false;
            }

            command = new TaskpaneWebCommand(
                "search", requestId, text, 3, null, TaskpaneSearchScope.AllMachine,
                String.Empty, immediate, null, -1, null, 0, null);
            return true;
        }

        private static bool TryParseSimpleAction(
            string type,
            IDictionary<string, object> payload,
            string requestId,
            out TaskpaneWebCommand command,
            out string error)
        {
            command = null;
            error = null;
            if (type == "retry")
            {
                if (HasUnexpectedKeys(payload, RetryPayloadAllowedKeys)) { error = "重试参数无效。"; return false; }
            }
            else if (payload.Count != 0) { error = "操作参数无效。"; return false; }
            string action = type == "browseScope" ? "browseScope" : (type == "retry" ? "retry" : "restoreScope");
            command = new TaskpaneWebCommand(
                type, requestId, null, -1, null, -1, null, true, action, -1, null, 0, null);
            return true;
        }

        private static TaskpaneWebCommand New(string type, string requestId)
        {
            return new TaskpaneWebCommand(
                type, requestId, null, -1, null, -1, null, false, null, -1, null, 0, null);
        }

        private static IDictionary<string, object> EmptyPayload(IDictionary<string, object> envelope)
        {
            object value;
            if (!envelope.TryGetValue("payload", out value) || value == null)
                return new Dictionary<string, object>(StringComparer.Ordinal);
            return value as IDictionary<string, object>;
        }

        private static bool ValidateLegacyKinds(IDictionary<string, object> payload)
        {
            object value;
            if (!payload.TryGetValue("kinds", out value) || value == null)
            {
                object typeValue;
                IDictionary<string, object> types;
                if (!payload.TryGetValue("types", out typeValue) ||
                    (types = typeValue as IDictionary<string, object>) == null) return true;
                if (HasUnexpectedKeys(types, TypeFilterAllowedKeys)) return false;
                bool ignored;
                return TryReadBoolean(types, "part", true, out ignored) &&
                    TryReadBoolean(types, "assembly", true, out ignored) &&
                    TryReadBoolean(types, "drawing", true, out ignored);
            }
            IEnumerable values = value as IEnumerable;
            if (values == null || value is string) return false;
            foreach (object item in values)
            {
                int kind;
                if (!TryConvertInt(item, out kind) || kind < 0 || kind > 2) return false;
            }
            return true;
        }

        private static bool TryReadBoolean(
            IDictionary<string, object> source,
            string key,
            bool fallback,
            out bool result)
        {
            result = fallback;
            object value;
            if (!source.TryGetValue(key, out value) || value == null) return true;
            if (!(value is bool)) return false;
            result = (bool)value;
            return true;
        }

        private static bool TryReadInt(IDictionary<string, object> source, string key, out int result)
        {
            result = 0;
            object value;
            return source.TryGetValue(key, out value) && TryConvertInt(value, out result);
        }

        private static bool TryReadOptionalInt(
            IDictionary<string, object> source,
            string key,
            int minimum,
            int maximum)
        {
            if (!source.ContainsKey(key)) return true;
            int value;
            return TryReadInt(source, key, out value) && value >= minimum && value <= maximum;
        }

        private static bool TryConvertInt(object value, out int result)
        {
            result = 0;
            if (value is int) { result = (int)value; return true; }
            if (value is long && (long)value >= Int32.MinValue && (long)value <= Int32.MaxValue)
            {
                result = (int)(long)value;
                return true;
            }
            return false;
        }

        private static string ReadString(IDictionary<string, object> source, string key, int maxLength)
        {
            object value;
            string text;
            if (!source.TryGetValue(key, out value) || (text = value as string) == null || text.Length > maxLength)
                return null;
            return text;
        }

        private static bool HasUnexpectedKeys(IDictionary<string, object> source, string[] allowed)
        {
            foreach (string key in source.Keys)
            {
                if (Array.IndexOf(allowed, key) < 0) return true;
            }
            return false;
        }

        private static bool IsRequestIdCharacter(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                   (value >= '0' && value <= '9') || value == '-' || value == '_' || value == '.';
        }

    }
}
