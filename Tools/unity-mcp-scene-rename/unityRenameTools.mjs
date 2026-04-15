import { z } from "zod";

const filterShape = {
  nameContains: z.string().optional().describe("Case-insensitive substring to match against GameObject names."),
  nameRegex: z.string().optional().describe("Case-insensitive regular expression to match against GameObject names."),
  pathContains: z.string().optional().describe("Case-insensitive substring to match against hierarchy paths such as Root/Child."),
  pathRegex: z.string().optional().describe("Case-insensitive regular expression to match against hierarchy paths."),
  tag: z.string().optional().describe("Exact Unity tag to match."),
  componentType: z.string().optional().describe("Exact component type name or full name, for example Camera or UnityEngine.Light."),
  includeInactive: z.boolean().optional().default(true).describe("Include inactive GameObjects. Defaults to true."),
  allowAll: z.boolean().optional().default(false).describe("Required when no filters are provided.")
};

const renameShape = {
  ...filterShape,
  template: z.string().describe("Rename template. Supports {name}, {index}, {index:00}, and {index:000}."),
  startIndex: z.number().int().optional().default(1).describe("Starting index for {index}. Defaults to 1."),
  step: z.number().int().optional().default(1).describe("Index increment after each object. Defaults to 1."),
  sortBy: z.enum(["hierarchy", "name", "path"]).optional().default("hierarchy").describe("Ordering used before applying indexes."),
  maxMatches: z.number().int().min(1).max(10000).optional().default(100).describe("Safety limit for matched objects.")
};

const regexRenameShape = {
  ...filterShape,
  searchRegex: z.string().describe("Regular expression applied to each matched GameObject name."),
  replacement: z.string().describe("Replacement text. Use JavaScript/C# style capture references such as $1 and $2."),
  maxMatches: z.number().int().min(1).max(10000).optional().default(100).describe("Safety limit for matched objects.")
};

export function registerUnityRenameTools(server, unityBaseUrl) {
  server.tool(
    "unity_list_scene_objects",
    "List matching GameObjects in the currently active Unity scene.",
    filterShape,
    async (args) => callUnityTool(unityBaseUrl, "list-scene-objects", args)
  );

  server.tool(
    "unity_preview_batch_rename",
    "Preview a template-based batch rename for matching GameObjects without modifying the Unity scene.",
    renameShape,
    async (args) => callUnityTool(unityBaseUrl, "preview-batch-rename", args)
  );

  server.tool(
    "unity_batch_rename_scene_objects",
    "Execute a template-based batch rename for matching GameObjects in the currently active Unity scene.",
    renameShape,
    async (args) => callUnityTool(unityBaseUrl, "batch-rename-scene-objects", args)
  );

  server.tool(
    "unity_preview_regex_rename",
    "Preview a regex replacement rename for matching GameObjects without modifying the Unity scene.",
    regexRenameShape,
    async (args) => callUnityTool(unityBaseUrl, "preview-regex-rename", args)
  );

  server.tool(
    "unity_regex_rename_scene_objects",
    "Execute a regex replacement rename for matching GameObjects in the currently active Unity scene.",
    regexRenameShape,
    async (args) => callUnityTool(unityBaseUrl, "regex-rename-scene-objects", args)
  );
}

async function callUnityTool(unityBaseUrl, endpoint, args) {
  try {
    const result = await postUnity(unityBaseUrl, endpoint, args);
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify(result, null, 2)
        }
      ]
    };
  } catch (error) {
    return {
      isError: true,
      content: [
        {
          type: "text",
          text: formatError(unityBaseUrl, error)
        }
      ]
    };
  }
}

async function postUnity(unityBaseUrl, endpoint, payload) {
  const response = await fetch(`${unityBaseUrl}/${endpoint}`, {
    method: "POST",
    headers: {
      "content-type": "application/json"
    },
    body: JSON.stringify(payload || {})
  });

  const text = await response.text();
  const data = parseJson(text);

  if (!response.ok || data?.ok === false) {
    const message = data?.error || text || `Unity returned HTTP ${response.status}`;
    throw new Error(message);
  }

  return data;
}

function parseJson(text) {
  if (!text) {
    return null;
  }

  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function formatError(unityBaseUrl, error) {
  const message = error instanceof Error ? error.message : String(error);
  return [
    "Unity scene rename tool failed.",
    "",
    `Unity endpoint: ${unityBaseUrl}`,
    `Reason: ${message}`,
    "",
    "Make sure the Unity project is open and the menu Tools > Unity MCP > Scene Rename Server > Status shows the server is running."
  ].join("\n");
}
