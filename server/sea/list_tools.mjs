import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
const cmd = process.argv[2], args = process.argv.slice(3);
const c = new Client({ name: "t", version: "1" });
await c.connect(new StdioClientTransport({ command: cmd, args, stderr: "ignore" }));
const r = await c.listTools();
console.log(cmd, "tools:", r.tools.length);
await c.close();
