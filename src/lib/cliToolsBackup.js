import fs from "fs/promises";
import path from "path";
import { DATA_DIR } from "./dataDir.js";

const getToolBackupDir = (toolId) => path.join(DATA_DIR, "cli-tool-backups", toolId);
const getManifestPath = (toolId) => path.join(getToolBackupDir(toolId), "manifest.json");

/**
 * Check if a backup exists for the given tool.
 * @param {string} toolId
 * @returns {Promise<boolean>}
 */
export async function hasToolBackup(toolId) {
  try {
    await fs.access(getManifestPath(toolId));
    return true;
  } catch {
    return false;
  }
}

/**
 * Get backup metadata for a tool.
 * @param {string} toolId
 * @returns {Promise<{ hasBackup: boolean, timestamp?: string }>}
 */
export async function getToolBackupInfo(toolId) {
  try {
    const raw = await fs.readFile(getManifestPath(toolId), "utf-8");
    const manifest = JSON.parse(raw);
    return { hasBackup: true, timestamp: manifest.timestamp };
  } catch {
    return { hasBackup: false };
  }
}

/**
 * Backup the specified files for a tool BEFORE modifying them.
 * If a backup already exists for this tool, it is preserved (to retain
 * the original configuration before FlagshipRouter was applied).
 *
 * @param {string} toolId - Tool identifier, e.g. "codex", "claude", "cline"
 * @param {Record<string, string>} filesMap - Map of key -> targetFilePath, e.g. { config: "/path/to/config.toml" }
 * @returns {Promise<boolean>} true if a new backup was created, false if already backed up
 */
export async function backupToolFiles(toolId, filesMap) {
  if (await hasToolBackup(toolId)) {
    return false;
  }

  const backupDir = getToolBackupDir(toolId);
  await fs.mkdir(backupDir, { recursive: true });

  const manifest = {
    toolId,
    timestamp: new Date().toISOString(),
    files: {},
  };

  for (const [key, targetPath] of Object.entries(filesMap)) {
    if (!targetPath) continue;

    let existed = false;
    let backupFileName = null;

    try {
      const content = await fs.readFile(targetPath);
      backupFileName = `${key}.bak`;
      await fs.writeFile(path.join(backupDir, backupFileName), content);
      existed = true;
    } catch (err) {
      if (err.code !== "ENOENT") {
        console.warn(`[cliToolsBackup] Warning reading ${targetPath}:`, err.message);
      }
      existed = false;
    }

    manifest.files[key] = {
      targetPath,
      backupFileName,
      existed,
    };
  }

  await fs.writeFile(getManifestPath(toolId), JSON.stringify(manifest, null, 2), "utf-8");
  return true;
}

/**
 * Restore the tool's original files from backup.
 * If a backup exists:
 * - Files that existed originally are restored to their exact contents.
 * - Files that did not exist originally (created by FlagshipRouter) are deleted.
 * - The backup directory and manifest are removed after successful restoration.
 *
 * @param {string} toolId - Tool identifier
 * @returns {Promise<{ restored: boolean, filesRestored: string[], filesRemoved: string[] }>}
 */
export async function restoreToolBackup(toolId) {
  if (!(await hasToolBackup(toolId))) {
    return { restored: false, filesRestored: [], filesRemoved: [] };
  }

  const backupDir = getToolBackupDir(toolId);
  const manifestRaw = await fs.readFile(getManifestPath(toolId), "utf-8");
  const manifest = JSON.parse(manifestRaw);

  const filesRestored = [];
  const filesRemoved = [];

  for (const [, fileInfo] of Object.entries(manifest.files || {})) {
    const { targetPath, backupFileName, existed } = fileInfo;
    if (!targetPath) continue;

    if (existed && backupFileName) {
      const backupFilePath = path.join(backupDir, backupFileName);
      try {
        const content = await fs.readFile(backupFilePath);
        await fs.mkdir(path.dirname(targetPath), { recursive: true });
        await fs.writeFile(targetPath, content);
        filesRestored.push(targetPath);
      } catch (err) {
        console.warn(`[cliToolsBackup] Failed restoring ${targetPath}:`, err.message);
      }
    } else if (!existed) {
      try {
        await fs.unlink(targetPath);
        filesRemoved.push(targetPath);
      } catch (err) {
        if (err.code !== "ENOENT") {
          console.warn(`[cliToolsBackup] Failed removing created file ${targetPath}:`, err.message);
        }
      }
    }
  }

  // Clean up backup directory
  try {
    const entries = await fs.readdir(backupDir);
    for (const entry of entries) {
      await fs.unlink(path.join(backupDir, entry));
    }
    await fs.rmdir(backupDir);
  } catch (err) {
    console.warn(`[cliToolsBackup] Error cleaning backup dir ${backupDir}:`, err.message);
  }

  return { restored: true, filesRestored, filesRemoved };
}
