import { copyFile, mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { dirname, isAbsolute, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { applyEdits, modify, parse, type ParseError } from "jsonc-parser";

export function addPlugin(source: string, configPath: string, pluginPath: string): string {
  const errors: ParseError[] = [];
  const config = parse(source, errors, { allowTrailingComma: true });
  if (errors.length || !config || typeof config !== "object" || Array.isArray(config)) {
    throw new Error("OpenCode configuration must be a valid JSONC object; it was not changed.");
  }
  if (config.plugins !== undefined && !Array.isArray(config.plugins)) {
    throw new Error("OpenCode V2 plugins must be an array; configuration was not changed.");
  }
  const canonical = (entry: unknown): string | undefined => {
    const specifier = typeof entry === "string" ? entry : entry && typeof entry === "object" ? (entry as { package?: unknown }).package : undefined;
    if (typeof specifier !== "string") return;
    try {
      if (specifier.startsWith("file:")) return resolve(fileURLToPath(specifier)).toLowerCase();
      if (isAbsolute(specifier) || specifier.startsWith(".")) return resolve(dirname(configPath), specifier).toLowerCase();
    } catch { /* Unrelated invalid entry is left untouched. */ }
  };
  const plugins: unknown[] = config.plugins ?? [];
  if (plugins.some((entry) => canonical(entry) === resolve(pluginPath).toLowerCase())) return source;
  const path = config.plugins ? ["plugins", plugins.length] : ["plugins"];
  const value = config.plugins ? pathToFileURL(pluginPath).href : [pathToFileURL(pluginPath).href];
  return applyEdits(source, modify(source, path, value, { formattingOptions: { insertSpaces: true, tabSize: 2, eol: source.includes("\r\n") ? "\r\n" : "\n" } }));
}

export async function registerPlugin(configPath: string): Promise<boolean> {
  const pluginPath = resolve(fileURLToPath(new URL("..", import.meta.url)));
  let source: string;
  let existed = true;
  try { source = await readFile(configPath, "utf8"); }
  catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    existed = false;
    source = '{\n  "$schema": "https://opencode.ai/config.json"\n}\n';
  }
  const next = addPlugin(source, configPath, pluginPath);
  if (next === source) return false;
  await mkdir(dirname(configPath), { recursive: true });
  if (existed) await copyFile(configPath, `${configPath}.cursor-backup-${Date.now()}`);
  const staging = `${configPath}.cursor-${process.pid}.tmp`;
  await writeFile(staging, next, { encoding: "utf8", flag: "wx" });
  await rename(staging, configPath);
  return true;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (!process.argv[2]) throw new Error("Pass an absolute opencode.json or opencode.jsonc path.");
  const path = resolve(process.argv[2]);
  console.log(`${await registerPlugin(path) ? "Registered" : "Already registered"}: ${path}`);
}
