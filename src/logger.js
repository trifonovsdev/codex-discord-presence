'use strict';

const fs = require('fs');

const DEFAULT_MAX_BYTES = 512 * 1024;

/**
 * Append-only file logger with size based rotation. A single previous
 * generation is kept so a long running daemon cannot fill the install
 * directory, which used to happen because presence.log grew forever.
 */
function createLogger(filePath, { maxBytes = DEFAULT_MAX_BYTES, clock = () => new Date() } = {}) {
  let written = null;

  function currentSize() {
    if (written !== null) return written;
    try {
      written = fs.statSync(filePath).size;
    } catch {
      written = 0;
    }
    return written;
  }

  function rotate() {
    try {
      fs.rmSync(`${filePath}.1`, { force: true });
      fs.renameSync(filePath, `${filePath}.1`);
    } catch {
      try {
        fs.rmSync(filePath, { force: true });
      } catch {}
    }
    written = 0;
  }

  return function log(message) {
    const line = `${clock().toISOString()} ${message}\n`;
    const bytes = Buffer.byteLength(line, 'utf8');
    try {
      if (currentSize() + bytes > maxBytes) rotate();
      fs.appendFileSync(filePath, line, 'utf8');
      written = currentSize() + bytes;
    } catch {
      written = null;
    }
  };
}

module.exports = { createLogger, DEFAULT_MAX_BYTES };
