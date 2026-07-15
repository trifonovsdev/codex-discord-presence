'use strict';

function configuredRemotes(config, defaultMonitorPath) {
  const output = [];
  const remoteConfig = config?.remote || {};
  const values = Array.isArray(remoteConfig.hosts) ? remoteConfig.hosts : [];
  for (const [index, item] of values.entries()) {
    if (!item || typeof item !== 'object') continue;
    const host = String(item.host || '').trim();
    const monitorPath = String(item.monitorPath || remoteConfig.monitorPath || defaultMonitorPath || '').trim();
    if (!/^[A-Za-z0-9._@:-]+$/.test(host) || !/^[A-Za-z0-9_./~-]+$/.test(monitorPath)) continue;
    output.push({
      name: String(item.name || host || `remote-${index + 1}`).slice(0, 60),
      host,
      monitorPath,
      roots: Array.isArray(item.roots) ? item.roots.map((root) => String(root).replace(/\/+$/, '')).filter(Boolean) : [],
    });
  }

  const legacyHost = String(remoteConfig.host || '').trim();
  const legacyPath = String(remoteConfig.monitorPath || defaultMonitorPath || '').trim();
  if (!output.length && /^[A-Za-z0-9._@:-]+$/.test(legacyHost) && /^[A-Za-z0-9_./~-]+$/.test(legacyPath)) {
    output.push({ name: legacyHost, host: legacyHost, monitorPath: legacyPath, roots: [] });
  }
  return output;
}

function remoteForCwd(cwd, remoteHosts) {
  if (!String(cwd || '').startsWith('/')) return null;
  const matches = remoteHosts
    .flatMap((remote) => remote.roots.length
      ? remote.roots.filter((root) => cwd === root || cwd.startsWith(`${root}/`)).map((root) => ({ remote, length: root.length }))
      : [{ remote, length: 0 }])
    .sort((left, right) => right.length - left.length);
  if (matches[0]?.length > 0) return matches[0].remote;
  return remoteHosts.length === 1 ? remoteHosts[0] : null;
}

module.exports = { configuredRemotes, remoteForCwd };
