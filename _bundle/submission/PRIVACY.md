# Revit MCP Privacy Policy

Effective 2026-11-13 · Publisher: Rita (Rirychh)

**What the add-in collects:** nothing. Revit MCP has no analytics, telemetry, accounts or servers operated by the publisher.

**Local connections:** the add-in runs an HTTP endpoint bound to 127.0.0.1 (ports 48884 and up), protected by a per-machine token. It is reachable only from the same computer. The bundled `revit-mcp-server.exe` talks to it and to Claude Desktop over standard input/output.

**AI providers:** when you use the AI Chat panel, your prompts and the parts of your Revit model needed to answer them are sent directly from your computer to the provider you select (Anthropic, OpenAI or Google) using your own API key. When you use Claude Desktop, the same data goes to Anthropic through Claude Desktop. Those providers' privacy policies apply.

**API keys:** stored in Windows Credential Manager on your computer and sent only to the matching provider.

**Files written:** settings in `%LOCALAPPDATA%\revit-mcp\`; when you click *Connect Claude Desktop*, an entry in `claude_desktop_config.json` (a `.bak-revitmcp` backup is made first).

**Contact:** https://github.com/Rirychh/revit-mcp/issues