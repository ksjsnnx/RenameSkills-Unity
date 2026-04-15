#!/usr/bin/env node

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerUnityRenameTools } from "./unityRenameTools.mjs";

const unityBaseUrl = (process.env.UNITY_MCP_URL || "http://127.0.0.1:8756").replace(/\/+$/, "");

const server = new McpServer({
  name: "unity-scene-rename",
  version: "1.0.0"
});

registerUnityRenameTools(server, unityBaseUrl);

const transport = new StdioServerTransport();
await server.connect(transport);
