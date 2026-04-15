using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class UnityMcpSceneRenameServer
{
    private const string Prefix = "http://127.0.0.1:8756/";
    private const int DefaultMaxMatches = 100;
    private const int HardMaxMatches = 10000;

    private static readonly object PendingLock = new object();
    private static readonly Queue<Action> PendingMainThreadActions = new Queue<Action>();
    private static readonly Regex TemplateTokenRegex = new Regex(@"\{([^{}]+)\}", RegexOptions.Compiled);
    private static readonly Regex IndexTokenRegex = new Regex(@"\{index(?::0+)?\}", RegexOptions.Compiled);

    private static HttpListener listener;
    private static Task listenerTask;
    private static string lastError;

    static UnityMcpSceneRenameServer()
    {
        EditorApplication.update -= RunPendingMainThreadActions;
        EditorApplication.update += RunPendingMainThreadActions;
        EditorApplication.quitting -= StopServer;
        EditorApplication.quitting += StopServer;
        StartServer();
    }

    [MenuItem("Tools/Unity MCP/Scene Rename Server/Start")]
    private static void StartServerMenuItem()
    {
        StartServer();
        ShowStatus();
    }

    [MenuItem("Tools/Unity MCP/Scene Rename Server/Stop")]
    private static void StopServerMenuItem()
    {
        StopServer();
        ShowStatus();
    }

    [MenuItem("Tools/Unity MCP/Scene Rename Server/Status")]
    private static void ShowStatus()
    {
        Scene scene = SceneManager.GetActiveScene();
        string sceneName = scene.IsValid() ? scene.name : "(no active scene)";
        string message = IsRunning
            ? "Scene rename server is running at " + Prefix + "\nActive scene: " + sceneName
            : "Scene rename server is stopped.";

        if (!string.IsNullOrEmpty(lastError))
        {
            message += "\n\nLast error: " + lastError;
        }

        EditorUtility.DisplayDialog("Unity MCP Scene Rename Server", message, "OK");
    }

    private static bool IsRunning
    {
        get { return listener != null && listener.IsListening; }
    }

    private static void StartServer()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            lastError = null;
            listener = new HttpListener();
            listener.Prefixes.Add(Prefix);
            listener.Start();
            listenerTask = Task.Run(ListenLoop);
            Debug.Log("[Unity MCP] Scene rename server started at " + Prefix);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            Debug.LogError("[Unity MCP] Failed to start scene rename server: " + ex);
            StopServer();
        }
    }

    private static void StopServer()
    {
        HttpListener activeListener = listener;
        listener = null;

        if (activeListener != null)
        {
            try
            {
                activeListener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        listenerTask = null;
    }

    private static async Task ListenLoop()
    {
        while (listener != null && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Debug.LogError("[Unity MCP] Listener error: " + ex);
                continue;
            }

            _ = Task.Run(delegate { return HandleContextAsync(context); });
        }
    }

    private static async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(path))
            {
                path = "/health";
            }

            if (context.Request.HttpMethod == "GET" && path == "/health")
            {
                Dictionary<string, object> health = await RunOnMainThread(BuildHealthResponse);
                WriteJson(context, 200, health);
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                throw new UserFacingException("Unsupported method. Use GET /health or POST tool endpoints.");
            }

            Dictionary<string, object> request = ReadJsonObject(context.Request);
            Dictionary<string, object> response;

            if (path == "/list-scene-objects")
            {
                response = await RunOnMainThread(delegate { return ListSceneObjects(request); });
            }
            else if (path == "/preview-batch-rename")
            {
                response = await RunOnMainThread(delegate { return PreviewBatchRename(request); });
            }
            else if (path == "/batch-rename-scene-objects")
            {
                response = await RunOnMainThread(delegate { return BatchRenameSceneObjects(request); });
            }
            else if (path == "/preview-regex-rename")
            {
                response = await RunOnMainThread(delegate { return PreviewRegexRename(request); });
            }
            else if (path == "/regex-rename-scene-objects")
            {
                response = await RunOnMainThread(delegate { return RegexRenameSceneObjects(request); });
            }
            else
            {
                throw new UserFacingException("Unknown endpoint: " + path);
            }

            WriteJson(context, 200, response);
        }
        catch (UserFacingException ex)
        {
            WriteJson(context, 400, ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            WriteJson(context, 500, ErrorResponse("Unity MCP server error: " + ex.Message));
            Debug.LogError("[Unity MCP] Request failed: " + ex);
        }
    }

    private static Dictionary<string, object> BuildHealthResponse()
    {
        Scene scene = SceneManager.GetActiveScene();
        return new Dictionary<string, object>
        {
            { "ok", true },
            { "server", "unity-mcp-scene-rename" },
            { "url", Prefix },
            { "activeScene", scene.IsValid() ? scene.name : null },
            { "sceneLoaded", scene.IsValid() && scene.isLoaded }
        };
    }

    private static Dictionary<string, object> ListSceneObjects(Dictionary<string, object> rawRequest)
    {
        RenameRequest request = RenameRequest.FromDictionary(rawRequest);
        List<SceneObjectInfo> matches = FindMatchingObjects(request);
        return new Dictionary<string, object>
        {
            { "ok", true },
            { "sceneName", GetActiveSceneOrThrow().name },
            { "count", matches.Count },
            { "objects", SerializeObjects(matches) }
        };
    }

    private static Dictionary<string, object> PreviewBatchRename(Dictionary<string, object> rawRequest)
    {
        RenameRequest request = RenameRequest.FromDictionary(rawRequest);
        List<RenamePlanItem> plan = BuildRenamePlan(request);
        return new Dictionary<string, object>
        {
            { "ok", true },
            { "sceneName", GetActiveSceneOrThrow().name },
            { "count", plan.Count },
            { "objects", SerializePlan(plan, false) }
        };
    }

    private static Dictionary<string, object> BatchRenameSceneObjects(Dictionary<string, object> rawRequest)
    {
        RenameRequest request = RenameRequest.FromDictionary(rawRequest);
        Scene scene = GetActiveSceneOrThrow();
        List<RenamePlanItem> plan = BuildRenamePlan(request);
        List<RenamePlanItem> changed = new List<RenamePlanItem>();

        Undo.SetCurrentGroupName("Unity MCP Batch Rename Scene Objects");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (RenamePlanItem item in plan)
        {
            if (item.OldName == item.NewName)
            {
                continue;
            }

            Undo.RecordObject(item.GameObject, "Rename Scene Object");
            item.GameObject.name = item.NewName;
            changed.Add(item);
        }

        Undo.CollapseUndoOperations(undoGroup);

        if (changed.Count > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
        }

        return new Dictionary<string, object>
        {
            { "ok", true },
            { "sceneName", scene.name },
            { "matchedCount", plan.Count },
            { "changedCount", changed.Count },
            { "objects", SerializePlan(changed, true) }
        };
    }

    private static Dictionary<string, object> PreviewRegexRename(Dictionary<string, object> rawRequest)
    {
        RenameRequest request = RenameRequest.FromDictionary(rawRequest);
        List<RenamePlanItem> plan = BuildRegexRenamePlan(request);
        return new Dictionary<string, object>
        {
            { "ok", true },
            { "sceneName", GetActiveSceneOrThrow().name },
            { "count", plan.Count },
            { "objects", SerializePlan(plan, false) }
        };
    }

    private static Dictionary<string, object> RegexRenameSceneObjects(Dictionary<string, object> rawRequest)
    {
        RenameRequest request = RenameRequest.FromDictionary(rawRequest);
        Scene scene = GetActiveSceneOrThrow();
        List<RenamePlanItem> plan = BuildRegexRenamePlan(request);
        List<RenamePlanItem> changed = new List<RenamePlanItem>();

        Undo.SetCurrentGroupName("Unity MCP Regex Rename Scene Objects");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (RenamePlanItem item in plan)
        {
            if (item.OldName == item.NewName)
            {
                continue;
            }

            Undo.RecordObject(item.GameObject, "Regex Rename Scene Object");
            item.GameObject.name = item.NewName;
            changed.Add(item);
        }

        Undo.CollapseUndoOperations(undoGroup);

        if (changed.Count > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
        }

        return new Dictionary<string, object>
        {
            { "ok", true },
            { "sceneName", scene.name },
            { "matchedCount", plan.Count },
            { "changedCount", changed.Count },
            { "objects", SerializePlan(changed, true) }
        };
    }

    private static List<RenamePlanItem> BuildRenamePlan(RenameRequest request)
    {
        if (string.IsNullOrEmpty(request.Template))
        {
            throw new UserFacingException("template is required.");
        }

        ValidateTemplate(request.Template);

        List<SceneObjectInfo> matches = FindMatchingObjects(request);
        if (matches.Count > request.MaxMatches)
        {
            throw new UserFacingException(
                "Matched " + matches.Count + " objects, which exceeds maxMatches=" + request.MaxMatches +
                ". Narrow the filters or increase maxMatches.");
        }

        if (matches.Count > 1 && !IndexTokenRegex.IsMatch(request.Template))
        {
            throw new UserFacingException("template must include {index}, {index:00}, or another index token when renaming multiple objects.");
        }

        SortMatches(matches, request.SortBy);

        List<RenamePlanItem> plan = new List<RenamePlanItem>();
        int index = request.StartIndex;
        foreach (SceneObjectInfo match in matches)
        {
            string newName = RenderTemplate(request.Template, match.Name, index);
            plan.Add(new RenamePlanItem
            {
                GameObject = match.GameObject,
                ObjectId = match.ObjectId,
                Path = match.Path,
                OldName = match.Name,
                NewName = newName
            });
            index += request.Step;
        }

        return plan;
    }

    private static List<RenamePlanItem> BuildRegexRenamePlan(RenameRequest request)
    {
        if (string.IsNullOrEmpty(request.SearchRegex))
        {
            throw new UserFacingException("searchRegex is required.");
        }

        if (request.Replacement == null)
        {
            throw new UserFacingException("replacement is required.");
        }

        Regex searchRegex = CompileOptionalRegex(request.SearchRegex, "searchRegex");
        List<SceneObjectInfo> matches = FindMatchingObjects(request);
        if (matches.Count > request.MaxMatches)
        {
            throw new UserFacingException(
                "Matched " + matches.Count + " objects, which exceeds maxMatches=" + request.MaxMatches +
                ". Narrow the filters or increase maxMatches.");
        }

        List<RenamePlanItem> plan = new List<RenamePlanItem>();
        foreach (SceneObjectInfo match in matches)
        {
            if (!searchRegex.IsMatch(match.Name))
            {
                continue;
            }

            plan.Add(new RenamePlanItem
            {
                GameObject = match.GameObject,
                ObjectId = match.ObjectId,
                Path = match.Path,
                OldName = match.Name,
                NewName = searchRegex.Replace(match.Name, request.Replacement)
            });
        }

        return plan;
    }

    private static List<SceneObjectInfo> FindMatchingObjects(RenameRequest request)
    {
        Scene scene = GetActiveSceneOrThrow();
        request.ValidateFilters();

        Regex nameRegex = CompileOptionalRegex(request.NameRegex, "nameRegex");
        Regex pathRegex = CompileOptionalRegex(request.PathRegex, "pathRegex");

        List<SceneObjectInfo> allObjects = new List<SceneObjectInfo>();
        int hierarchyIndex = 0;
        GameObject[] roots = scene.GetRootGameObjects();

        foreach (GameObject root in roots)
        {
            Traverse(root, string.Empty, request.IncludeInactive, allObjects, ref hierarchyIndex);
        }

        List<SceneObjectInfo> matches = new List<SceneObjectInfo>();
        foreach (SceneObjectInfo info in allObjects)
        {
            if (!Matches(info.Name, request.NameContains, nameRegex))
            {
                continue;
            }

            if (!Matches(info.Path, request.PathContains, pathRegex))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(request.Tag) && !string.Equals(info.Tag, request.Tag, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(request.ComponentType) && !HasComponentType(info.GameObject, request.ComponentType))
            {
                continue;
            }

            matches.Add(info);
        }

        return matches;
    }

    private static void Traverse(GameObject gameObject, string parentPath, bool includeInactive, List<SceneObjectInfo> objects, ref int hierarchyIndex)
    {
        if (!includeInactive && !gameObject.activeInHierarchy)
        {
            return;
        }

        string path = string.IsNullOrEmpty(parentPath) ? gameObject.name : parentPath + "/" + gameObject.name;
        objects.Add(new SceneObjectInfo
        {
            GameObject = gameObject,
            ObjectId = gameObject.GetInstanceID(),
            Name = gameObject.name,
            Path = path,
            Tag = gameObject.tag,
            ActiveSelf = gameObject.activeSelf,
            ActiveInHierarchy = gameObject.activeInHierarchy,
            HierarchyIndex = hierarchyIndex,
            ComponentTypes = GetComponentTypeNames(gameObject)
        });
        hierarchyIndex++;

        Transform transform = gameObject.transform;
        for (int i = 0; i < transform.childCount; i++)
        {
            Traverse(transform.GetChild(i).gameObject, path, includeInactive, objects, ref hierarchyIndex);
        }
    }

    private static Scene GetActiveSceneOrThrow()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded)
        {
            throw new UserFacingException("No valid active scene is loaded.");
        }

        return scene;
    }

    private static Regex CompileOptionalRegex(string pattern, string fieldName)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException ex)
        {
            throw new UserFacingException(fieldName + " is not a valid regular expression: " + ex.Message);
        }
    }

    private static bool Matches(string value, string contains, Regex regex)
    {
        if (!string.IsNullOrEmpty(contains) &&
            value.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (regex != null && !regex.IsMatch(value))
        {
            return false;
        }

        return true;
    }

    private static bool HasComponentType(GameObject gameObject, string componentType)
    {
        Component[] components = gameObject.GetComponents<Component>();
        foreach (Component component in components)
        {
            if (component == null)
            {
                continue;
            }

            Type type = component.GetType();
            if (string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<object> GetComponentTypeNames(GameObject gameObject)
    {
        List<object> names = new List<object>();
        Component[] components = gameObject.GetComponents<Component>();
        foreach (Component component in components)
        {
            names.Add(component == null ? "MissingScript" : component.GetType().Name);
        }

        return names;
    }

    private static void SortMatches(List<SceneObjectInfo> matches, string sortBy)
    {
        if (string.Equals(sortBy, "name", StringComparison.OrdinalIgnoreCase))
        {
            matches.Sort(delegate (SceneObjectInfo left, SceneObjectInfo right)
            {
                int nameResult = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                return nameResult != 0 ? nameResult : left.HierarchyIndex.CompareTo(right.HierarchyIndex);
            });
            return;
        }

        if (string.Equals(sortBy, "path", StringComparison.OrdinalIgnoreCase))
        {
            matches.Sort(delegate (SceneObjectInfo left, SceneObjectInfo right)
            {
                int pathResult = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
                return pathResult != 0 ? pathResult : left.HierarchyIndex.CompareTo(right.HierarchyIndex);
            });
            return;
        }

        if (!string.Equals(sortBy, "hierarchy", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException("sortBy must be one of: hierarchy, name, path.");
        }
    }

    private static void ValidateTemplate(string template)
    {
        MatchCollection matches = TemplateTokenRegex.Matches(template);
        foreach (Match match in matches)
        {
            string token = match.Groups[1].Value;
            bool allowed = token == "name" || token == "index" || Regex.IsMatch(token, @"^index:0+$");
            if (!allowed)
            {
                throw new UserFacingException("Unsupported template token {" + token + "}. Use {name}, {index}, {index:00}, or {index:000}.");
            }
        }
    }

    private static string RenderTemplate(string template, string oldName, int index)
    {
        return TemplateTokenRegex.Replace(template, delegate (Match match)
        {
            string token = match.Groups[1].Value;
            if (token == "name")
            {
                return oldName;
            }

            if (token == "index")
            {
                return index.ToString(CultureInfo.InvariantCulture);
            }

            if (token.StartsWith("index:", StringComparison.Ordinal))
            {
                int width = token.Length - "index:".Length;
                return index.ToString("D" + width, CultureInfo.InvariantCulture);
            }

            return match.Value;
        });
    }

    private static List<object> SerializeObjects(List<SceneObjectInfo> objects)
    {
        List<object> serialized = new List<object>();
        foreach (SceneObjectInfo info in objects)
        {
            serialized.Add(new Dictionary<string, object>
            {
                { "objectId", info.ObjectId },
                { "path", info.Path },
                { "name", info.Name },
                { "tag", info.Tag },
                { "activeSelf", info.ActiveSelf },
                { "activeInHierarchy", info.ActiveInHierarchy },
                { "componentTypes", info.ComponentTypes }
            });
        }

        return serialized;
    }

    private static List<object> SerializePlan(List<RenamePlanItem> plan, bool includeChanged)
    {
        List<object> serialized = new List<object>();
        foreach (RenamePlanItem item in plan)
        {
            Dictionary<string, object> value = new Dictionary<string, object>
            {
                { "objectId", item.ObjectId },
                { "path", item.Path },
                { "oldName", item.OldName },
                { "newName", item.NewName }
            };

            if (includeChanged)
            {
                value.Add("changed", item.OldName != item.NewName);
            }

            serialized.Add(value);
        }

        return serialized;
    }

    private static Dictionary<string, object> ErrorResponse(string message)
    {
        return new Dictionary<string, object>
        {
            { "ok", false },
            { "error", message }
        };
    }

    private static Dictionary<string, object> ReadJsonObject(HttpListenerRequest request)
    {
        using (StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
        {
            string body = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(body))
            {
                return new Dictionary<string, object>();
            }

            object parsed = MiniJson.Deserialize(body);
            Dictionary<string, object> result = parsed as Dictionary<string, object>;
            if (result == null)
            {
                throw new UserFacingException("Request body must be a JSON object.");
            }

            return result;
        }
    }

    private static void WriteJson(HttpListenerContext context, int statusCode, Dictionary<string, object> value)
    {
        string json = MiniJson.Serialize(value);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    private static Task<T> RunOnMainThread<T>(Func<T> action)
    {
        TaskCompletionSource<T> completionSource = new TaskCompletionSource<T>();
        lock (PendingLock)
        {
            PendingMainThreadActions.Enqueue(delegate
            {
                try
                {
                    completionSource.SetResult(action());
                }
                catch (Exception ex)
                {
                    completionSource.SetException(ex);
                }
            });
        }

        return completionSource.Task;
    }

    private static void RunPendingMainThreadActions()
    {
        while (true)
        {
            Action action = null;
            lock (PendingLock)
            {
                if (PendingMainThreadActions.Count == 0)
                {
                    break;
                }

                action = PendingMainThreadActions.Dequeue();
            }

            action();
        }
    }

    private sealed class RenameRequest
    {
        public string NameContains;
        public string NameRegex;
        public string PathContains;
        public string PathRegex;
        public string Tag;
        public string ComponentType;
        public bool IncludeInactive = true;
        public bool AllowAll;
        public string Template;
        public int StartIndex = 1;
        public int Step = 1;
        public string SortBy = "hierarchy";
        public int MaxMatches = DefaultMaxMatches;
        public string SearchRegex;
        public string Replacement;

        public static RenameRequest FromDictionary(Dictionary<string, object> values)
        {
            RenameRequest request = new RenameRequest
            {
                NameContains = GetString(values, "nameContains"),
                NameRegex = GetString(values, "nameRegex"),
                PathContains = GetString(values, "pathContains"),
                PathRegex = GetString(values, "pathRegex"),
                Tag = GetString(values, "tag"),
                ComponentType = GetString(values, "componentType"),
                IncludeInactive = GetBool(values, "includeInactive", true),
                AllowAll = GetBool(values, "allowAll", false),
                Template = GetString(values, "template"),
                StartIndex = GetInt(values, "startIndex", 1),
                Step = GetInt(values, "step", 1),
                SortBy = GetString(values, "sortBy") ?? "hierarchy",
                MaxMatches = GetInt(values, "maxMatches", DefaultMaxMatches),
                SearchRegex = GetString(values, "searchRegex"),
                Replacement = GetString(values, "replacement")
            };

            if (request.MaxMatches < 1 || request.MaxMatches > HardMaxMatches)
            {
                throw new UserFacingException("maxMatches must be between 1 and " + HardMaxMatches + ".");
            }

            return request;
        }

        public void ValidateFilters()
        {
            if (!HasAnyFilter() && !AllowAll)
            {
                throw new UserFacingException("At least one filter is required unless allowAll=true is provided.");
            }
        }

        private bool HasAnyFilter()
        {
            return !string.IsNullOrEmpty(NameContains) ||
                   !string.IsNullOrEmpty(NameRegex) ||
                   !string.IsNullOrEmpty(PathContains) ||
                   !string.IsNullOrEmpty(PathRegex) ||
                   !string.IsNullOrEmpty(Tag) ||
                   !string.IsNullOrEmpty(ComponentType);
        }

        private static string GetString(Dictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(Dictionary<string, object> values, string key, bool defaultValue)
        {
            object value;
            if (!values.TryGetValue(key, out value) || value == null)
            {
                return defaultValue;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : defaultValue;
        }

        private static int GetInt(Dictionary<string, object> values, string key, int defaultValue)
        {
            object value;
            if (!values.TryGetValue(key, out value) || value == null)
            {
                return defaultValue;
            }

            if (value is int)
            {
                return (int)value;
            }

            if (value is long)
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            if (value is double)
            {
                return Convert.ToInt32((double)value, CultureInfo.InvariantCulture);
            }

            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : defaultValue;
        }
    }

    private sealed class SceneObjectInfo
    {
        public GameObject GameObject;
        public int ObjectId;
        public string Name;
        public string Path;
        public string Tag;
        public bool ActiveSelf;
        public bool ActiveInHierarchy;
        public int HierarchyIndex;
        public List<object> ComponentTypes;
    }

    private sealed class RenamePlanItem
    {
        public GameObject GameObject;
        public int ObjectId;
        public string Path;
        public string OldName;
        public string NewName;
    }

    private sealed class UserFacingException : Exception
    {
        public UserFacingException(string message) : base(message)
        {
        }
    }

    private static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (json == null)
            {
                return null;
            }

            return Parser.Parse(json);
        }

        public static string Serialize(object obj)
        {
            return Serializer.Serialize(obj);
        }

        private sealed class Parser : IDisposable
        {
            private const string WordBreak = "{}[],:\"";
            private readonly StringReader json;

            private Parser(string jsonString)
            {
                json = new StringReader(jsonString);
            }

            private enum Token
            {
                None,
                CurlyOpen,
                CurlyClose,
                SquaredOpen,
                SquaredClose,
                Colon,
                Comma,
                String,
                Number,
                True,
                False,
                Null
            }

            public static object Parse(string jsonString)
            {
                using (Parser instance = new Parser(jsonString))
                {
                    return instance.ParseValue();
                }
            }

            public void Dispose()
            {
                json.Dispose();
            }

            private Dictionary<string, object> ParseObject()
            {
                Dictionary<string, object> table = new Dictionary<string, object>();
                json.Read();

                while (true)
                {
                    switch (NextToken)
                    {
                        case Token.None:
                            return null;
                        case Token.Comma:
                            continue;
                        case Token.CurlyClose:
                            return table;
                    }

                    string name = ParseString();
                    if (name == null)
                    {
                        return null;
                    }

                    if (NextToken != Token.Colon)
                    {
                        return null;
                    }

                    json.Read();
                    table[name] = ParseValue();
                }
            }

            private List<object> ParseArray()
            {
                List<object> array = new List<object>();
                json.Read();

                bool parsing = true;
                while (parsing)
                {
                    Token nextToken = NextToken;
                    switch (nextToken)
                    {
                        case Token.None:
                            return null;
                        case Token.Comma:
                            continue;
                        case Token.SquaredClose:
                            parsing = false;
                            break;
                        default:
                            array.Add(ParseByToken(nextToken));
                            break;
                    }
                }

                return array;
            }

            private object ParseValue()
            {
                Token nextToken = NextToken;
                return ParseByToken(nextToken);
            }

            private object ParseByToken(Token token)
            {
                switch (token)
                {
                    case Token.String:
                        return ParseString();
                    case Token.Number:
                        return ParseNumber();
                    case Token.CurlyOpen:
                        return ParseObject();
                    case Token.SquaredOpen:
                        return ParseArray();
                    case Token.True:
                        return true;
                    case Token.False:
                        return false;
                    case Token.Null:
                        return null;
                    default:
                        return null;
                }
            }

            private string ParseString()
            {
                StringBuilder builder = new StringBuilder();
                json.Read();

                bool parsing = true;
                while (parsing)
                {
                    if (json.Peek() == -1)
                    {
                        break;
                    }

                    char c = NextChar;
                    switch (c)
                    {
                        case '"':
                            parsing = false;
                            break;
                        case '\\':
                            if (json.Peek() == -1)
                            {
                                parsing = false;
                                break;
                            }

                            c = NextChar;
                            switch (c)
                            {
                                case '"':
                                case '\\':
                                case '/':
                                    builder.Append(c);
                                    break;
                                case 'b':
                                    builder.Append('\b');
                                    break;
                                case 'f':
                                    builder.Append('\f');
                                    break;
                                case 'n':
                                    builder.Append('\n');
                                    break;
                                case 'r':
                                    builder.Append('\r');
                                    break;
                                case 't':
                                    builder.Append('\t');
                                    break;
                                case 'u':
                                    char[] hex = new char[4];
                                    for (int i = 0; i < 4; i++)
                                    {
                                        hex[i] = NextChar;
                                    }

                                    builder.Append((char)Convert.ToInt32(new string(hex), 16));
                                    break;
                            }
                            break;
                        default:
                            builder.Append(c);
                            break;
                    }
                }

                return builder.ToString();
            }

            private object ParseNumber()
            {
                string number = NextWord;
                if (number.IndexOf('.') == -1)
                {
                    long parsedInt;
                    if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedInt))
                    {
                        return parsedInt;
                    }
                }

                double parsedDouble;
                double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedDouble);
                return parsedDouble;
            }

            private void EatWhitespace()
            {
                while (json.Peek() != -1 && char.IsWhiteSpace(PeekChar))
                {
                    json.Read();
                }
            }

            private char PeekChar
            {
                get { return Convert.ToChar(json.Peek()); }
            }

            private char NextChar
            {
                get { return Convert.ToChar(json.Read()); }
            }

            private string NextWord
            {
                get
                {
                    StringBuilder builder = new StringBuilder();
                    while (json.Peek() != -1 && !IsWordBreak(PeekChar))
                    {
                        builder.Append(NextChar);
                    }

                    return builder.ToString();
                }
            }

            private Token NextToken
            {
                get
                {
                    EatWhitespace();
                    if (json.Peek() == -1)
                    {
                        return Token.None;
                    }

                    switch (PeekChar)
                    {
                        case '{':
                            return Token.CurlyOpen;
                        case '}':
                            json.Read();
                            return Token.CurlyClose;
                        case '[':
                            return Token.SquaredOpen;
                        case ']':
                            json.Read();
                            return Token.SquaredClose;
                        case ',':
                            json.Read();
                            return Token.Comma;
                        case '"':
                            return Token.String;
                        case ':':
                            return Token.Colon;
                        case '0':
                        case '1':
                        case '2':
                        case '3':
                        case '4':
                        case '5':
                        case '6':
                        case '7':
                        case '8':
                        case '9':
                        case '-':
                            return Token.Number;
                    }

                    switch (NextWord)
                    {
                        case "false":
                            return Token.False;
                        case "true":
                            return Token.True;
                        case "null":
                            return Token.Null;
                    }

                    return Token.None;
                }
            }

            private static bool IsWordBreak(char c)
            {
                return char.IsWhiteSpace(c) || WordBreak.IndexOf(c) != -1;
            }
        }

        private sealed class Serializer
        {
            private readonly StringBuilder builder;

            private Serializer()
            {
                builder = new StringBuilder();
            }

            public static string Serialize(object obj)
            {
                Serializer instance = new Serializer();
                instance.SerializeValue(obj);
                return instance.builder.ToString();
            }

            private void SerializeValue(object value)
            {
                string stringValue = value as string;
                if (stringValue != null)
                {
                    SerializeString(stringValue);
                    return;
                }

                if (value == null)
                {
                    builder.Append("null");
                    return;
                }

                if (value is bool)
                {
                    builder.Append((bool)value ? "true" : "false");
                    return;
                }

                IList list = value as IList;
                if (list != null)
                {
                    SerializeArray(list);
                    return;
                }

                IDictionary dictionary = value as IDictionary;
                if (dictionary != null)
                {
                    SerializeObject(dictionary);
                    return;
                }

                if (value is char)
                {
                    SerializeString(new string((char)value, 1));
                    return;
                }

                SerializeOther(value);
            }

            private void SerializeObject(IDictionary value)
            {
                bool first = true;
                builder.Append('{');

                foreach (object key in value.Keys)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    SerializeString(Convert.ToString(key, CultureInfo.InvariantCulture));
                    builder.Append(':');
                    SerializeValue(value[key]);
                    first = false;
                }

                builder.Append('}');
            }

            private void SerializeArray(IList array)
            {
                builder.Append('[');
                bool first = true;

                foreach (object obj in array)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    SerializeValue(obj);
                    first = false;
                }

                builder.Append(']');
            }

            private void SerializeString(string value)
            {
                builder.Append('"');

                char[] charArray = value.ToCharArray();
                foreach (char c in charArray)
                {
                    switch (c)
                    {
                        case '"':
                            builder.Append("\\\"");
                            break;
                        case '\\':
                            builder.Append("\\\\");
                            break;
                        case '\b':
                            builder.Append("\\b");
                            break;
                        case '\f':
                            builder.Append("\\f");
                            break;
                        case '\n':
                            builder.Append("\\n");
                            break;
                        case '\r':
                            builder.Append("\\r");
                            break;
                        case '\t':
                            builder.Append("\\t");
                            break;
                        default:
                            int codepoint = Convert.ToInt32(c);
                            if (codepoint >= 32 && codepoint <= 126)
                            {
                                builder.Append(c);
                            }
                            else
                            {
                                builder.Append("\\u");
                                builder.Append(codepoint.ToString("x4", CultureInfo.InvariantCulture));
                            }
                            break;
                    }
                }

                builder.Append('"');
            }

            private void SerializeOther(object value)
            {
                if (value is float || value is int || value is uint || value is long || value is double ||
                    value is sbyte || value is byte || value is short || value is ushort || value is ulong ||
                    value is decimal)
                {
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                }

                SerializeString(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }
    }
}
